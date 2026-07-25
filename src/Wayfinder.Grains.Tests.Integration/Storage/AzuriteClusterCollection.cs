using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Storage
{
    // Own collection, deliberately separate from SiloFixture.ClusterCollection: that fixture's
    // TestCluster is wired to in-memory journaled-grain storage, this one to Azure Blob/Azurite -
    // sharing a collection would let one silo configuration bleed into the other's tests.
    [CollectionDefinition(Name)]
    public class AzuriteClusterCollection : ICollectionFixture<AzuriteClusterFixture>
    {
        public const string Name = "AzuriteClusterCollection";
    }
}
