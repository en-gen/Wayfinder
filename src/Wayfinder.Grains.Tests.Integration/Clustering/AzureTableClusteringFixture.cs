using System;
using System.Net;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Azure.Identity;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Tests.Integration.Storage;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Clustering.AzureStorage;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.TestingHost;
using Serilog;
using Testcontainers.Azurite;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Clustering
{
    // Real Azure Table clustering validation for work item #30's deployed-cluster path
    // (Wayfinder.Silo/Program.cs ConfigureDeployedOrleans): a MULTI-silo TestCluster whose membership
    // is NOT TestCluster's own in-memory dev membership oracle but the real
    // UseAzureStorageClustering provider pointed at Azurite's table endpoint - the crux proof
    // that ConfigureDeployedOrleans's clustering choice actually forms a cluster against a real
    // Azure Table membership store, not just compiles. Reminders/storage/streams are
    // deliberately NOT wired here (see ClusterMembershipTestGrain) - this suite is scoped to
    // clustering only.
    //
    // Own fixture/collection, separate from SiloFixture.ClusterFixture and
    // Storage.AzuriteClusterFixture: both of those run with TestClusterOptions'
    // UseTestClusterMembership left at its default (true - TestCluster's own in-memory dev
    // membership), so mixing this fixture's real-membership silos into either collection would
    // risk cross-configuration bleed.
    //
    // Azurite is a throwaway Docker container this fixture owns, mirroring
    // Storage.AzuriteClusterFixture exactly (see that file for the
    // AzuriteBuilder/--skipApiVersionCheck rationale) - started fresh per run, no fixed host
    // port, no manual docker compose step.
    public class AzureTableClusteringFixture : IDisposable, IAsyncLifetime
    {
        private const string AzuriteImage = "mcr.microsoft.com/azure-storage/azurite:3.35.0";

        // Mirrors Wayfinder.Silo's AzureOptions.ClusteringSectionKey by value - the test assembly has
        // no reference to the silo project. The registration path built from it
        // (TestSiloConfigurator/TestClientConfigurator below) must match Program.cs's
        // ConfigureDeployedOrleans/ConfigureServices exactly (AddTableServiceClient section-shape
        // auth, UseAzureStorageClustering resolving TableServiceClient from DI).
        private const string ClusteringSectionKey = "Azure:Clustering";

        // Multi-silo cluster: >= 2 InitialSilosCount so real Azure Table membership actually has
        // more than one silo to reconcile - a single-silo cluster would never exercise the
        // membership table's read/write-back/gossip path at all.
        private const short InitialSilosCount = 2;

        private const string NotStartedMessage =
            "Azurite TestCluster was not started: DockerAvailability.IsAvailable was false at " +
            "fixture initialization. Tests using this fixture must be gated with " +
            "[RequiresDockerFact], which consults the same process-cached availability decision.";

        private AzuriteContainer _azurite;
        private TestCluster _cluster;
        private IClusterClient _clusterClient;
        private bool _disposed;

        public TestCluster Cluster => _cluster ?? throw new InvalidOperationException(NotStartedMessage);
        public IClusterClient ClusterClient => _clusterClient ?? throw new InvalidOperationException(NotStartedMessage);

        // A collection fixture is constructed/disposed once per collection regardless of whether
        // any member test actually executes - RequiresDockerFact.Skip only stops individual
        // [RequiresDockerFact] test methods from running, it does not stop xunit from still
        // calling this. Consults the SAME process-cached decision as RequiresDockerFact (see
        // Storage.DockerAvailability): cached-false means every [RequiresDockerFact] test is
        // skipped, so no-op here; cached-true means the Azurite container and cluster MUST come
        // up - if StartAsync/DeployAsync throws against a Docker daemon that answered the
        // availability ping but then broke, that is a real failure deliberately left to
        // propagate loudly.
        public async Task InitializeAsync()
        {
            if (!DockerAvailability.IsAvailable)
            {
                return;
            }

            // --skipApiVersionCheck mirrors Storage.AzuriteClusterFixture - without it Azurite
            // rejects the SDK's x-ms-version header outright.
            _azurite = new AzuriteBuilder(AzuriteImage)
                .WithCommand("--skipApiVersionCheck")
                .Build();

            await _azurite.StartAsync();

            // Covers blob AND table AND queue endpoints - only the table endpoint is actually
            // exercised by this suite, via the Azure:Clustering:connectionString property below.
            var connectionString = _azurite.GetConnectionString();

            var builder = new TestClusterBuilder(InitialSilosCount);

            builder.Options.ClusterId = "azure-table-clustering-integration";
            builder.Options.ServiceId = "Case.Flow";

            // The knob that actually matters here: TestClusterBuilder defaults
            // UseTestClusterMembership to true (TestCluster's own in-memory dev membership
            // oracle), which would silently shadow the UseAzureStorageClustering registration in
            // TestSiloConfigurator below. Setting it false is what forces the cluster to form
            // membership through the real Azure Table provider instead - this is the crux
            // validation for work item #30.
            builder.Options.UseTestClusterMembership = false;

            // Hierarchical keys land in each silo's/client's IConfiguration as the same
            // Azure:Clustering section shape Wayfinder.Silo reads from configuration (see AzureOptions
            // and Program.cs ConfigureServices/ConfigureDeployedOrleans), so the configurators
            // below can consume it through the identical registration path.
            builder.Properties[ClusteringSectionKey + ":connectionString"] = connectionString;

            builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
            builder.AddClientBuilderConfigurator<TestClientConfigurator>();

            _cluster = builder.Build();

            await _cluster.DeployAsync();

            // Real Azure Table membership needs the silos to actually gossip/reconcile through
            // the table before every silo reliably reports as Active - unlike the default
            // in-memory test membership, this is not instantaneous. DeployAsync starting cleanly
            // does not by itself guarantee every silo has converged on a consistent cluster view
            // yet, so wait explicitly before handing out the client.
            await _cluster.WaitForLivenessToStabilizeAsync();

            _clusterClient = _cluster.Client;
        }

        public async Task DisposeAsync()
        {
            if (_cluster != null)
            {
                _cluster.StopAllSilos();
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

        // Mirrors Program.cs's ConfigureDeployedOrleans clustering wiring (UseAzureStorageClustering
        // resolving TableServiceClient from DI, itself registered via AddTableServiceClient from
        // the Azure:Clustering config section) adapted to TestCluster's class-based
        // ISiloConfigurator. Deliberately does NOT wire reminders/storage/streams - out of scope
        // for this suite (see ClusterMembershipTestGrain).
        private class TestSiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder silo)
            {
                silo.Services.AddAzureClients(azure =>
                {
                    azure.UseCredential(new DefaultAzureCredential());

                    azure.AddTableServiceClient(silo.Configuration.GetRequiredSection(ClusteringSectionKey));
                });

                silo
                    .UseAzureStorageClustering(ConfigureClustering)

                    .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)

                    .ConfigureLogging(IntegrationTestLogging.Configure);

                // TestCluster's codegen scans every [Alias]-tagged grain interface in this test
                // assembly when the silo starts - not just the ones this suite's own grain uses -
                // so any CMMN-model-touching interface elsewhere in Wayfinder.Grains.Tests.Integration
                // still needs this fallback registered here too (same reasoning as
                // Storage.AzuriteClusterFixture's TestSiloConfigurator and
                // SiloFixture.ClusterFixture's TestSiloConfigurator).
                silo.Services.AddSerializer(s => s.AddJsonSerializer(
                    isSupported: OrleansFallbackJsonSerializer.IsSupportedType,
                    jsonSerializerOptions: OrleansFallbackJsonSerializer.Options()));
            }

            private static void ConfigureClustering(OptionsBuilder<AzureStorageClusteringOptions> options) =>
                options.Configure<TableServiceClient>((clusteringOptions, tableServiceClient) =>
                    clusteringOptions.TableServiceClient = tableServiceClient);

        }

        // The client-side counterpart: with UseTestClusterMembership = false, the client can no
        // longer rely on TestCluster's own shortcut for discovering gateways, so it needs the
        // matching UseAzureStorageClustering registration (client-side AzureStorageGatewayOptions,
        // not AzureStorageClusteringOptions - same shape, different type per Orleans'
        // client/silo split) pointed at the same Azure Table.
        private class TestClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.Services.AddAzureClients(azure =>
                {
                    azure.UseCredential(new DefaultAzureCredential());

                    azure.AddTableServiceClient(configuration.GetRequiredSection(ClusteringSectionKey));
                });

                clientBuilder.UseAzureStorageClustering(ConfigureGateway);

                clientBuilder.Services.AddSerializer(s => s.AddJsonSerializer(
                    isSupported: OrleansFallbackJsonSerializer.IsSupportedType,
                    jsonSerializerOptions: OrleansFallbackJsonSerializer.Options()));
            }

            private static void ConfigureGateway(OptionsBuilder<AzureStorageGatewayOptions> options) =>
                options.Configure<TableServiceClient>((gatewayOptions, tableServiceClient) =>
                    gatewayOptions.TableServiceClient = tableServiceClient);
        }
    }
}
