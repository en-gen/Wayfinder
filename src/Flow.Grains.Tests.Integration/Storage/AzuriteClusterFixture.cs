using System;
using System.Net;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Storage.Blobs;
using Flow.Grains.Interfaces.Model;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Exceptions;
using Xunit;

namespace Flow.Grains.Tests.Integration.Storage
{
    // Self-contained fixture for the Azurite restart-survival suite (work item #54). Deliberately
    // does not touch SiloFixture.ClusterFixture: everything here mirrors that fixture's memory
    // registrations for PubSubStore/streams/serializer (ClusterFixture's own comment explains why
    // the silo AND client configurators both need OrleansFallbackJsonSerializer) EXCEPT the
    // journaled-grain storage slot, which points at Azure Blob/Azurite instead of memory.
    //
    // Azurite connection: 127.0.0.1:10000 (blob), well-known devstoreaccount1 dev credentials.
    // Container name is suffixed with a fresh guid per fixture instance so parallel/repeated test
    // runs never collide over the same container; DisposeAsync deletes it.
    public class AzuriteClusterFixture : IDisposable, IAsyncLifetime
    {
        private const string AzuriteConnectionString = "UseDevelopmentStorage=true";

        // Parity with Flow.Silo by value: the test assembly has no reference to the silo
        // project, so these mirror AzureOptions.StorageSectionKey and
        // nameof(AzureOptions.StorageOptions.CaseStateContainer). The registration path built
        // from them (TestSiloConfigurator below) must match Program.cs exactly.
        private const string StorageSectionKey = "Azure:Storage";
        private const string CaseStateContainerKey = "Azure:Storage:CaseStateContainer";
        private const string CaseStateContainerClientName = "CaseStateContainer";

        // Defense-in-depth against fail-open (PR !16 build 47: the discovery-time gate said
        // reachable, the fixture-init probe said unreachable, and tests dereferenced a
        // silently-null ClusterClient - a bare NullReferenceException). With the probe decision
        // now process-cached the gate and this fixture cannot disagree, but if anything ever
        // reaches these accessors without the cluster started (e.g. a test in this collection
        // not gated by [AzuriteFact]), fail LOUD with the reason instead of returning null.
        private const string NotStartedMessage =
            "Azurite TestCluster was not started: AzuriteProbe.IsAvailable was false at fixture " +
            "initialization. Tests using this fixture must be gated with [AzuriteFact], which " +
            "consults the same process-cached probe decision.";

        private TestCluster _cluster;
        private IClusterClient _clusterClient;
        private bool _disposed;

        public TestCluster Cluster => _cluster ?? throw new InvalidOperationException(NotStartedMessage);
        public IClusterClient ClusterClient => _clusterClient ?? throw new InvalidOperationException(NotStartedMessage);
        public string ContainerName { get; }

        public AzuriteClusterFixture()
        {
            ContainerName = $"azurite-journal-test-{Guid.NewGuid():N}";
        }

        // A collection fixture is constructed/disposed once per collection regardless of
        // whether any member test actually executes - AzuriteFact.Skip only stops individual
        // [AzuriteFact] test methods from running, it does not stop xunit from still calling
        // this. Consults the SAME process-cached decision as AzuriteFact (see AzuriteProbe):
        // cached-false means every [AzuriteFact] test is skipped, so no-op here; cached-true
        // means the cluster MUST come up - if Deploy() throws against a reachable-then-broken
        // Azurite, that is a real failure deliberately left to propagate loudly.
        public Task InitializeAsync()
        {
            if (!AzuriteProbe.IsAvailable)
            {
                return Task.CompletedTask;
            }

            var builder = new TestClusterBuilder();

            builder.Options.ClusterId = "azurite-integration";
            builder.Options.ServiceId = "Case.Flow";

            // Hierarchical keys land in each silo's IConfiguration as the same Azure:Storage
            // section shape Flow.Silo reads from appsettings.Development.json, so the
            // configurator below can consume it through the identical registration path.
            builder.Properties[StorageSectionKey + ":connectionString"] = AzuriteConnectionString;
            builder.Properties[CaseStateContainerKey] = ContainerName;

            builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
            builder.AddClientBuilderConfigurator<TestClientConfigurator>();

            _cluster = builder.Build();
            _cluster.Deploy();

            _clusterClient = _cluster.Client;

            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            if (_cluster == null)
            {
                return;
            }

            _cluster.StopAllSilos();

            var blobServiceClient = new BlobServiceClient(AzuriteConnectionString);
            await blobServiceClient.GetBlobContainerClient(ContainerName).DeleteIfExistsAsync();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                Log.CloseAndFlush();
            }

            _disposed = true;
        }

        // Mirrors ClusterFixture.TestSiloConfigurator except the journaled-grain storage slot:
        // AddAzureBlobGrainStorageAsDefault (Azurite) in place of AddMemoryGrainStorageAsDefault.
        // See Flow.Silo/Program.cs ConfigureDevelopmentOrleans for the citation establishing that
        // the "AsDefault" slot - not a same-named "LogStorage" registration - is what
        // [LogConsistencyProvider("LogStorage")] grains with no [StorageProvider] attribute
        // actually resolve.
        private class TestSiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder silo)
            {
                // TestClusterBuilder.Properties (set in InitializeAsync above) flows into each
                // silo's IConfiguration via TestClusterBuilder.Build()'s
                // configBuilder.AddInMemoryCollection(this.Properties) - the supported extension
                // point for passing fixture-level values into a class-based ISiloConfigurator,
                // per TestClusterBuilder.Properties's own doc comment ("Configuration values which
                // will be provided to the silos and clients created by this builder").
                //
                // This block is the Flow.Silo/Program.cs ConfigureServices registration path,
                // byte-for-byte except the container name source (IConfiguration key rather than
                // IOptionsMonitor<AzureOptions> - the silo's options type isn't referenced here):
                // section-shaped client registration, explicit UseCredential fallback, and the
                // named case-state BlobContainerClient the Orleans container factory resolves.
                silo.Services.AddAzureClients(azure =>
                {
                    azure.UseCredential(new DefaultAzureCredential());

                    azure.AddBlobServiceClient(silo.Configuration.GetRequiredSection(StorageSectionKey));

                    azure.AddClient<BlobContainerClient, BlobClientOptions>((_, provider) =>
                            provider.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(
                                provider.GetRequiredService<IConfiguration>()[CaseStateContainerKey]))
                        .WithName(CaseStateContainerClientName);
                });

                silo
                    .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)

                    .AddAzureBlobGrainStorageAsDefault(ConfigureBlobStorage)
                    .AddLogStorageBasedLogConsistencyProvider() // journaled grain
                    .AddMemoryGrainStorage("PubSubStore") // stream storage
                    .AddMemoryStreams("Default") // cluster stream provider
                    .UseInMemoryReminderService()

                    .ConfigureLogging(ConfigureLogging);

                silo.Services.AddSerializer(s => s.AddJsonSerializer(
                    isSupported: OrleansFallbackJsonSerializer.IsSupportedType,
                    jsonSerializerOptions: OrleansFallbackJsonSerializer.Options()));
            }

            // Same wiring as Flow.Silo/Program.cs ConfigureBlobStorage and must match it - see
            // there for the shipped-bits citations (grain-storage serializer default
            // determination, AzureBlobStorageOptions.BuildContainerFactory /
            // IBlobContainerFactory contract) and the JsonNode journal-state rationale (work
            // item #16).
            private static void ConfigureBlobStorage(OptionsBuilder<AzureBlobStorageOptions> options) =>
                options.Configure<Serializer, BlobServiceClient, IConfiguration>(
                    (storageOptions, serializer, blobServiceClient, configuration) =>
                    {
                        storageOptions.BlobServiceClient = blobServiceClient;
                        storageOptions.ContainerName = configuration[CaseStateContainerKey];
                        storageOptions.BuildContainerFactory = static (provider, _) =>
                            new CaseStateContainerFactory(provider.GetRequiredService<IAzureClientFactory<BlobContainerClient>>());
                        storageOptions.GrainStorageSerializer = new OrleansGrainStorageSerializer(serializer);
                    });

            private static void ConfigureLogging(ILoggingBuilder logging)
            {
                var levelSwitch = new LoggingLevelSwitch
                {
                    MinimumLevel = LogEventLevel.Debug
                };

                logging.AddSerilog(new LoggerConfiguration()
                    .MinimumLevel.ControlledBy(levelSwitch)
                    .Enrich.FromLogContext()
                    .Enrich.WithExceptionDetails()
                    .WriteTo.Seq(
                        "http://localhost:5341",
                        controlLevelSwitch: levelSwitch
                    )
                    .CreateLogger());
            }
        }

        // Mirror of Flow.Silo/Program.cs CaseStateContainerFactory (the test assembly has no
        // reference to the silo project) - must match it: routes Orleans' journaled-grain
        // persistence onto the named case-state BlobContainerClient registered above.
        // InitializeAsync mirrors DefaultBlobContainerFactory's create-if-not-exists behavior;
        // the BlobServiceClient parameter is unused because the container client already
        // carries the full pipeline.
        private sealed class CaseStateContainerFactory : IBlobContainerFactory
        {
            private readonly BlobContainerClient _container;

            public CaseStateContainerFactory(IAzureClientFactory<BlobContainerClient> containerClients)
            {
                _container = (containerClients ?? throw new ArgumentNullException(nameof(containerClients)))
                    .CreateClient(CaseStateContainerClientName);
            }

            public BlobContainerClient GetBlobContainerClient(GrainId grainId) => _container;

            public Task InitializeAsync(BlobServiceClient client) => _container.CreateIfNotExistsAsync();
        }

        // TestCluster's in-process client independently validates serializer coverage for every
        // type reachable from grain interfaces (see ClusterFixture's identical comment) - this
        // suite's test grain is CMMN-free, but the client still loads Flow.Grains.Interfaces.Model
        // types transitively, so the same fallback registration is required here too.
        private class TestClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.AddMemoryStreams("Default");

                clientBuilder.Services.AddSerializer(s => s.AddJsonSerializer(
                    isSupported: OrleansFallbackJsonSerializer.IsSupportedType,
                    jsonSerializerOptions: OrleansFallbackJsonSerializer.Options()));
            }
        }
    }
}
