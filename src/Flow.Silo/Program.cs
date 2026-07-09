using System;
using System.Net;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Storage.Blobs;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Infrastructure.Quartz;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Services.PlanItemBehaviorConfigurator;
using Flow.Grains.Services.PlanItemStateMachineConfigurator;
using Flow.Silo.Infrastructure.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Exceptions;
using HostBuilderContext = Microsoft.Extensions.Hosting.HostBuilderContext;

namespace Flow.Silo
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            await CreateHostBuilder(args).Build().RunAsync();
        }

        private static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                })
                .ConfigureLogging(ConfigureLogging)
                .UseOrleans(ConfigureOrleans)
                .ConfigureServices(ConfigureServices)
                .UseConsoleLifetime();

        private static void ConfigureLogging(HostBuilderContext context, ILoggingBuilder logging)
        {
            var seqOptions = context.Configuration.GetSection(SeqOptions.ConfigKey).Get<SeqOptions>();

            var levelSwitch = new LoggingLevelSwitch
            {
                MinimumLevel = LogEventLevel.Debug
            };

            logging.AddSerilog(new LoggerConfiguration()
                .MinimumLevel.ControlledBy(levelSwitch)
                .Enrich.FromLogContext()
                .Enrich.WithExceptionDetails()
                .WriteTo.Seq(
                    serverUrl: seqOptions.ServerUrl,
                    apiKey: seqOptions.ApiKey,
                    controlLevelSwitch: levelSwitch
                )
                .CreateLogger());
        }

        private static void ConfigureOrleans(HostBuilderContext context, ISiloBuilder silo)
        {
            if (context.HostingEnvironment.IsDevelopment())
            {
                ConfigureDevelopmentOrleans(context, silo);
            }
            else
            {
                ConfigureDeployedOrleans(context, silo);
            }

            silo
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = context.HostingEnvironment.EnvironmentName;
                    options.ServiceId = context.HostingEnvironment.ApplicationName;
                });

            // Fallback serializer for types Orleans's [GenerateSerializer] codegen can't reasonably
            // cover: the XSD-generated CMMN model (Flow.Grains.Interfaces.Model). Everything else in
            // the solution is swept with [GenerateSerializer] + [Id(n)] and uses the native serializer.
            // See OrleansFallbackJsonSerializer for the shared isSupported predicate/options - the
            // TestCluster silo and client configurators in ClusterFixture must register identically.
            silo.Services.AddSerializer(s => s.AddJsonSerializer(
                isSupported: OrleansFallbackJsonSerializer.IsSupportedType,
                jsonSerializerOptions: OrleansFallbackJsonSerializer.Options()));
        }

        private static void ConfigureDevelopmentOrleans(HostBuilderContext context, ISiloBuilder silo)
        {
            silo
                .UseLocalhostClustering()

                .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)

                // Journaled-grain (event-sourced) storage backend, Azure Blob via Azurite locally.
                //
                // CmmnElementGrain carries [LogConsistencyProvider(ProviderName = "LogStorage")] and
                // NO [StorageProvider] attribute. Per Orleans 10.2.1 source
                // (dotnet/orleans, src/Orleans.EventSourcing/LogConsistency/LogConsistentGrain.cs,
                // LogConsistentGrain<TView>.OnSetupState):
                //
                //   ILogViewAdaptorFactory consistencyProvider = SetupLogConsistencyProvider(grainContext);
                //   IGrainStorage grainStorage = consistencyProvider.UsesStorageProvider
                //       ? GrainStorageHelpers.GetGrainStorage(grainContext?.GrainInstance.GetType(), this.ServiceProvider)
                //       : null;
                //
                // and GrainStorageHelpers.GetGrainStorage (src/Orleans.Core/Providers/GrainStorageHelpers.cs):
                //
                //   var attr = grainType.GetCustomAttributes(typeof(StorageProviderAttribute), true) ...;
                //   var storageProvider = attr != null
                //       ? services.GetKeyedService<IGrainStorage>(attr.ProviderName)
                //       : services.GetService<IGrainStorage>();   // <- no [StorageProvider]: unkeyed default
                //
                // So "LogStorage" (the ProviderName on [LogConsistencyProvider]) only selects which
                // ILogViewAdaptorFactory runs the log-view protocol (a separate keyed registration -
                // see AddLogStorageBasedLogConsistencyProvider below); it is NOT the name storage is
                // resolved under. With no [StorageProvider] attribute anywhere on CmmnElementGrain or
                // its subclasses, the storage lookup is the unkeyed services.GetService<IGrainStorage>()
                // call - i.e. whatever is registered as the silo's DEFAULT grain storage
                // (ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME). AddAzureBlobGrainStorageAsDefault
                // registers exactly that slot (src/Azure/Orleans.Persistence.AzureStorage/Hosting/
                // AzureBlobSiloBuilderExtensions.cs), which is why it - not a same-named
                // AddAzureBlobGrainStorage("LogStorage", ...) - is the correct call here. Empirically
                // confirmed by Flow.Grains.Tests.Integration's Azurite restart-survival suite: state
                // rehydrates from the journal after DeactivateOnIdle, and the blob container actually
                // holds the grain's blob (see AzuriteJournalStorageTests).
                .AddAzureBlobGrainStorageAsDefault(ConfigureBlobStorage)
                .AddLogStorageBasedLogConsistencyProvider() // journaled grain: selects the LogStorage log-view adaptor: chooses HOW the view is replicated, not WHERE it is stored (see comment above)
                .AddMemoryGrainStorage("PubSubStore", ConfigureMemoryStorage) // stream storage
                .AddMemoryStreams("Default") // cluster stream provider (replaces removed AddSimpleMessageStreamProvider)
                .UseInMemoryReminderService();
        }

        // Everything here resolves from DI - no hand-constructed clients:
        //
        // - BlobServiceClient / the named case-state BlobContainerClient come from the
        //   AddAzureClients registration in ConfigureServices below; BuildContainerFactory is
        //   AzureBlobStorageOptions' hook for supplying the container (verified against the
        //   shipped Orleans.Persistence.AzureStorage 10.2.1: IBlobContainerFactory exposes
        //   GetBlobContainerClient(GrainId) + InitializeAsync(BlobServiceClient), and the
        //   default factory just does GetBlobContainerClient(options.ContainerName) +
        //   CreateIfNotExistsAsync). ContainerName is still set from the same options value the
        //   named client uses so the validator and the silo options dump stay truthful - the
        //   factory, not ContainerName, is what actually selects the container.
        //
        // - GrainStorageSerializer pin: grain-STORAGE serialization is a separate serializer
        //   stack from the Orleans wire serializer. Left unset, AzureBlobStorageOptions
        //   .GrainStorageSerializer (null by default) is filled by
        //   DefaultStorageProviderSerializerOptionsConfigurator<TOptions>.PostConfigure - only
        //   when null, keyed-then-unkeyed IGrainStorageSerializer from DI - and the silo's
        //   unkeyed default in Orleans 10.2.1 is JsonGrainStorageSerializer:
        //
        //     services.TryAddSingleton<IGrainStorageSerializer, JsonGrainStorageSerializer>();
        //       (Orleans.Hosting.DefaultSiloServices.AddDefaultServices, decompiled from
        //        Orleans.Runtime 10.2.1; the configurator and JsonGrainStorageSerializer
        //        decompiled from Orleans.Core 10.2.1 - the latter wraps OrleansJsonSerializer,
        //        i.e. reflection-based Newtonsoft.Json)
        //
        //   That reflection-JSON stack cannot round-trip System.Text.Json.Nodes values held in
        //   journaled grain state: a JsonArray anywhere in state fails the storage write, which
        //   the log-consistency LogViewAdaptor retries indefinitely - the grain call never
        //   returns (work item #16; JsonNodeOrleansSerializationTests). CaseFileItemStore
        //   journals a bare JsonNode Value through THIS registration in Development, so the
        //   serializer is pinned to OrleansGrainStorageSerializer: grain-state persistence rides
        //   the same Orleans serializer as the wire, which handles the JsonNode family natively
        //   (STJ JsonCodec, registered in ConfigureOrleans above).
        //
        // Must match AzuriteClusterFixture's TestSiloConfigurator identically; the blob
        // round-trip is pinned by AzuriteJournalStorageTests' JsonNode-document test.
        private static void ConfigureBlobStorage(OptionsBuilder<AzureBlobStorageOptions> options) =>
            options.Configure<Serializer, BlobServiceClient, IOptionsMonitor<AzureOptions>>(
                (storageOptions, serializer, blobServiceClient, azureOptions) =>
                {
                    storageOptions.BlobServiceClient = blobServiceClient;
                    storageOptions.ContainerName = azureOptions.CurrentValue.Storage.CaseStateContainer;
                    storageOptions.BuildContainerFactory = static (provider, _) =>
                        new CaseStateContainerFactory(provider.GetRequiredService<IAzureClientFactory<BlobContainerClient>>());
                    storageOptions.GrainStorageSerializer = new OrleansGrainStorageSerializer(serializer);
                });

        // Routes Orleans' journaled-grain persistence onto the SAME named BlobContainerClient
        // the rest of the app would resolve (AddAzureClients WithName registration below) -
        // single source of truth for the case-state container. InitializeAsync mirrors
        // DefaultBlobContainerFactory's create-if-not-exists behavior; the BlobServiceClient
        // parameter is unused because the container client already carries the full pipeline.
        private sealed class CaseStateContainerFactory : IBlobContainerFactory
        {
            private readonly BlobContainerClient _container;

            public CaseStateContainerFactory(IAzureClientFactory<BlobContainerClient> containerClients)
            {
                _container = (containerClients ?? throw new ArgumentNullException(nameof(containerClients)))
                    .CreateClient(nameof(AzureOptions.StorageOptions.CaseStateContainer));
            }

            public BlobContainerClient GetBlobContainerClient(GrainId grainId) => _container;

            public Task InitializeAsync(BlobServiceClient client) => _container.CreateIfNotExistsAsync();
        }

        // MemoryGrainStorage's default IGrainStorageSerializer is JsonGrainStorageSerializer - a
        // reflection-based JSON storage serializer that is a SEPARATE stack from the Orleans wire
        // serializer (and from the fallback JSON codec registered in ConfigureOrleans above). It
        // cannot round-trip System.Text.Json.Nodes values held in grain state: journaling a
        // CaseFileItemStore whose Value contains a JsonArray fails inside the storage write, which
        // the log-consistency protocol (LogViewAdaptor) retries indefinitely - the grain call
        // never completes (see work item #16 and JsonNodeOrleansSerializationTests). Pinning the
        // storage serializer to OrleansGrainStorageSerializer routes grain-state persistence
        // through the same Orleans serializer used on the wire, which handles the JsonNode family
        // natively. Must match ClusterFixture's TestSiloConfigurator identically.
        private static void ConfigureMemoryStorage(OptionsBuilder<MemoryGrainStorageOptions> options) =>
            options.Configure<Serializer>((storageOptions, serializer) =>
                storageOptions.GrainStorageSerializer = new OrleansGrainStorageSerializer(serializer));

        private static void ConfigureDeployedOrleans(HostBuilderContext context, ISiloBuilder silo)
        {
            throw new NotImplementedException();
        }

        private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
        {
            services.Configure<AzureOptions>(context.Configuration.GetSection(AzureOptions.ConfigKey));

            // Azure SDK clients from the Azure:Storage config SECTION - environment-agnostic by
            // construction, which is why this lives host-level rather than in the
            // environment-specific Orleans wiring: the section's SHAPE picks the auth mode
            // (connectionString -> connection-string client, our local/Azurite case; serviceUri +
            // credential -> TokenCredential client, the deployed shape) so the deployed flip is
            // config-only. See AzureOptions.StorageSectionKey for the full key contract and the
            // shipped-bits citations. GetRequiredSection fails fast at startup if the section is
            // missing entirely - deliberately, since a silo without storage config can only limp
            // until first persistence.
            //
            // UseCredential(new DefaultAzureCredential()) pins the fallback TokenCredential for
            // clients whose section carries no credential keys. Verified against the shipped
            // Microsoft.Extensions.Azure 1.14.0 (decompiled): a per-client section credential,
            // when present, wins - AzureClientFactoryBuilder.RegisterClientFactory(IConfiguration)
            // attaches it via WithCredential - and the global factory is only consulted otherwise
            // (AzureClientsGlobalOptions.CredentialFactory, whose library default is already
            // DefaultAzureCredential - the explicit line pins that choice rather than introducing
            // it). Connection-string clients never request a token, and DefaultAzureCredential
            // probes its chain only on first token request, so this is inert in Development.
            //
            // The named BlobContainerClient is the single source of truth for the case-state
            // container: Orleans' storage rides it via CaseStateContainerFactory (see
            // ConfigureBlobStorage above), and any future app-level consumer resolves the same
            // client via IAzureClientFactory<BlobContainerClient>.CreateClient(
            // nameof(AzureOptions.StorageOptions.CaseStateContainer)).
            services.AddAzureClients(azure =>
            {
                azure.UseCredential(new DefaultAzureCredential());

                azure.AddBlobServiceClient(context.Configuration.GetRequiredSection(AzureOptions.StorageSectionKey));

                azure.AddClient<BlobContainerClient, BlobClientOptions>((_, provider) =>
                        provider.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(
                            provider.GetRequiredService<IOptionsMonitor<AzureOptions>>().CurrentValue.Storage.CaseStateContainer))
                    .WithName(nameof(AzureOptions.StorageOptions.CaseStateContainer));
            });

            services
                .AddRuleExecutor()
                .AddSingleton<IPlanItemBehaviorConfigurator, PlanItemBehaviorConfiguratorService>()
                .AddSingleton<IPlanItemStateMachineConfigurator, PlanItemStateMachineConfiguratorService>()
                .AddQuartz(QuartzSchedulerConfig.Volatile);
        }
    }
}
