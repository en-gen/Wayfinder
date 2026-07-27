using System;
using System.Net;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Storage.Blobs;
using Wayfinder.Grains.Interfaces.Model;
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
using Testcontainers.Azurite;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Storage
{
    // Self-contained fixture for the Azurite restart-survival suite (work item #54, Testcontainers-
    // provisioned per work item #60). Deliberately does not touch SiloFixture.ClusterFixture:
    // everything here mirrors that fixture's memory registrations for PubSubStore/streams/serializer
    // (ClusterFixture's own comment explains why the silo AND client configurators both need
    // OrleansFallbackJsonSerializer) EXCEPT the journaled-grain storage slot, which points at Azure
    // Blob/Azurite instead of memory.
    //
    // Azurite is a throwaway Docker container this fixture owns (see AzuriteImage below), started
    // fresh per run - no fixed host port, no clash with a developer's own Azurite, no manual
    // docker compose step. Container name is suffixed with a fresh guid per fixture instance so
    // parallel/repeated test runs never collide over the same blob container either; DisposeAsync
    // deletes it before tearing down the Azurite container itself.
    public class AzuriteClusterFixture : IDisposable, IAsyncLifetime
    {
        // Same pinned tag CI used for the old manually-started container - recent enough that
        // Azure.Storage.Blobs's x-ms-version header is accepted once --skipApiVersionCheck (below)
        // is also passed; Azurite's version gate otherwise rejects newer SDK requests
        // (Azure/Azurite#2562, #2564, #2626).
        private const string AzuriteImage = "mcr.microsoft.com/azure-storage/azurite:3.35.0";

        // Parity with Wayfinder.Silo by value: the test assembly has no reference to the silo
        // project, so these mirror AzureOptions.StorageSectionKey and
        // nameof(AzureOptions.StorageOptions.CaseStateContainer). The registration path built
        // from them (TestSiloConfigurator below) must match Program.cs exactly.
        private const string StorageSectionKey = "Azure:Storage";
        private const string CaseStateContainerKey = "Azure:Storage:CaseStateContainer";
        private const string CaseStateContainerClientName = "CaseStateContainer";

        // Defense-in-depth against fail-open (PR !16 build 47: the discovery-time gate said
        // reachable, the fixture-init probe said unreachable, and tests dereferenced a
        // silently-null ClusterClient - a bare NullReferenceException). With the availability
        // decision process-cached (DockerAvailability) the gate and this fixture cannot disagree,
        // but if anything ever reaches these accessors without the cluster started (e.g. a test in
        // this collection not gated by [RequiresDockerFact]), fail LOUD with the reason instead of
        // returning null.
        private const string NotStartedMessage =
            "Azurite TestCluster was not started: DockerAvailability.IsAvailable was false at " +
            "fixture initialization. Tests using this fixture must be gated with " +
            "[RequiresDockerFact], which consults the same process-cached availability decision.";

        private AzuriteContainer _azurite;
        private string _connectionString;
        private BlobServiceClient _blobServiceClient;
        private TestCluster _cluster;
        private IClusterClient _clusterClient;
        private bool _disposed;

        public TestCluster Cluster => _cluster ?? throw new InvalidOperationException(NotStartedMessage);
        public IClusterClient ClusterClient => _clusterClient ?? throw new InvalidOperationException(NotStartedMessage);
        public string ContainerName { get; }

        // The live connection string / blob client for the Azurite container this fixture just
        // started - dynamic (random host port per run), unlike the old fixed-:10000 const, so
        // tests must read it from here rather than hardcoding "UseDevelopmentStorage=true".
        public string ConnectionString => _connectionString ?? throw new InvalidOperationException(NotStartedMessage);
        public BlobServiceClient BlobServiceClient => _blobServiceClient ?? throw new InvalidOperationException(NotStartedMessage);

        public AzuriteClusterFixture()
        {
            ContainerName = $"azurite-journal-test-{Guid.NewGuid():N}";
        }

        // A collection fixture is constructed/disposed once per collection regardless of
        // whether any member test actually executes - RequiresDockerFact.Skip only stops
        // individual [RequiresDockerFact] test methods from running, it does not stop xunit from
        // still calling this. Consults the SAME process-cached decision as RequiresDockerFact
        // (see DockerAvailability): cached-false means every [RequiresDockerFact] test is
        // skipped, so no-op here; cached-true means the Azurite container and cluster MUST come
        // up - if StartAsync/Deploy() throws against a Docker daemon that answered the
        // availability ping but then broke, that is a real failure deliberately left to
        // propagate loudly.
        public async Task InitializeAsync()
        {
            if (!DockerAvailability.IsAvailable)
            {
                return;
            }

            // --skipApiVersionCheck mirrors the old CI-started container's flag (see AzuriteImage)
            // - without it Azurite rejects the SDK's x-ms-version header outright.
            _azurite = new AzuriteBuilder(AzuriteImage)
                .WithCommand("--skipApiVersionCheck")
                .Build();

            await _azurite.StartAsync();

            _connectionString = _azurite.GetConnectionString();
            _blobServiceClient = new BlobServiceClient(_connectionString);

            var builder = new TestClusterBuilder();

            builder.Options.ClusterId = "azurite-integration";
            builder.Options.ServiceId = "Wayfinder";

            // Hierarchical keys land in each silo's IConfiguration as the same Azure:Storage
            // section shape Wayfinder.Silo reads from appsettings.Development.json, so the
            // configurator below can consume it through the identical registration path.
            builder.Properties[StorageSectionKey + ":connectionString"] = _connectionString;
            builder.Properties[CaseStateContainerKey] = ContainerName;

            builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
            builder.AddClientBuilderConfigurator<TestClientConfigurator>();

            _cluster = builder.Build();
            _cluster.Deploy();

            _clusterClient = _cluster.Client;
        }

        public async Task DisposeAsync()
        {
            if (_cluster != null)
            {
                _cluster.StopAllSilos();

                await _blobServiceClient.GetBlobContainerClient(ContainerName).DeleteIfExistsAsync();
            }

            if (_azurite != null)
            {
                await _azurite.DisposeAsync();
            }
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
        // See Wayfinder.Silo/Program.cs ConfigureDevelopmentOrleans for the citation establishing that
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
                // This block is the Wayfinder.Silo/Program.cs ConfigureServices registration path,
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

                    .ConfigureLogging(IntegrationTestLogging.Configure);

                silo.Services.AddSerializer(s => s.AddJsonSerializer(
                    isSupported: OrleansFallbackJsonSerializer.IsSupportedType,
                    jsonSerializerOptions: OrleansFallbackJsonSerializer.Options()));
            }

            // Same wiring as Wayfinder.Silo/Program.cs ConfigureBlobStorage and must match it - see
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

        }

        // Mirror of Wayfinder.Silo/Program.cs CaseStateContainerFactory (the test assembly has no
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
        // suite's test grain is CMMN-free, but the client still loads Wayfinder.Grains.Interfaces.Model
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
