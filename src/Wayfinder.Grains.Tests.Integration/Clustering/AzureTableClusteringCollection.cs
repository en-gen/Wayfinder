using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Clustering
{
    // Own collection, deliberately separate from SiloFixture.ClusterCollection and
    // Storage.AzuriteClusterCollection: this fixture's TestCluster runs with
    // TestClusterOptions.UseTestClusterMembership = false (real Azure Table membership) - sharing
    // a collection with fixtures that leave it at the default (true, TestCluster's own in-memory
    // dev membership) would let one cluster-membership configuration bleed into the other's tests.
    [CollectionDefinition(Name)]
    public class AzureTableClusteringCollection : ICollectionFixture<AzureTableClusteringFixture>
    {
        public const string Name = "AzureTableClusteringCollection";
    }
}
