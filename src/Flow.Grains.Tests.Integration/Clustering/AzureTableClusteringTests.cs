using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Flow.Grains.Tests.Integration.Storage;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace Flow.Grains.Tests.Integration.Clustering
{
    // Crux validation for work item #30's deployed-cluster path (Flow.Silo/Program.cs
    // ConfigureDeployedOrleans): proves UseAzureStorageClustering actually forms a REAL Orleans
    // cluster via Azure Table membership (Azurite's table endpoint here / a real Azure Storage
    // account table endpoint when deployed), not merely that it compiles or starts a single silo.
    //
    // Opt-in via RequiresDockerFact: skipped (not failed) when Docker isn't reachable. Azurite
    // itself is self-provisioned by AzureTableClusteringFixture via Testcontainers - no manual
    // docker compose step and no fixed port to collide with a developer's own Azurite.
    [Collection(AzureTableClusteringCollection.Name)]
    public class AzureTableClusteringTests
    {
        private readonly IClusterClient _clusterClient;
        private readonly TestCluster _cluster;

        public AzureTableClusteringTests(AzureTableClusteringFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;
            _cluster = fixture.Cluster;
        }

        [RequiresDockerFact]
        public async Task GetHosts__Given_TwoSilosUsingAzureStorageClustering__When_Deployed__Then_BothReportActiveInTheRealTableMembershipStore()
        {
            // IManagementGrain.GetHosts reads the SAME membership store the silos themselves
            // gossip/reconcile through (Orleans.Clustering.AzureStorage's IMembershipTable), so a
            // count >= 2 here is proof the cluster's membership view - not just each silo's own
            // local process - was actually built from the real Azure Table, matching what a
            // second, independently-started silo/container would see if it queried the same table.
            var managementGrain = _clusterClient.GetGrain<IManagementGrain>(0);

            var hosts = await managementGrain.GetHosts(onlyActive: true);

            hosts.Should().HaveCountGreaterThanOrEqualTo(2,
                "ConfigureDeployedOrleans's UseAzureStorageClustering must register every silo as " +
                "Active in the real Azure Table membership store, not just start isolated processes " +
                "that never learn about each other");

            hosts.Values.Should().OnlyContain(status => status == SiloStatus.Active,
                "every silo the real Azure Table membership store knows about should have converged " +
                "to Active by the time the cluster finished deploying");

            // Belt-and-braces: TestCluster's own bookkeeping (independent of the management grain
            // call above) should agree that both silos are up.
            _cluster.GetActiveSilos().Count().Should().BeGreaterThanOrEqualTo(2);
        }

        [RequiresDockerFact]
        public async Task GetSiloAddress__Given_RealAzureTableClusterMembership__When_GrainCallMade__Then_Succeeds()
        {
            // A successful grain call end-to-end through the client - which itself only found a
            // gateway by reading the real Azure Table (see AzureTableClusteringFixture's
            // TestClientConfigurator) - is the second half of the crux proof: the cluster is not
            // just VISIBLE as formed, it is actually SERVING grain calls through that membership.
            var grain = _clusterClient.GetGrain<IClusterMembershipTestGrain>(1);

            var siloAddress = await grain.GetSiloAddress();

            siloAddress.Should().NotBeNullOrWhiteSpace();
        }
    }
}
