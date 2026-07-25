using Xunit;

namespace Wayfinder.Grains.Tests.Integration.SiloFixture
{
    [CollectionDefinition(ClusterCollection.Name)]
    public class ClusterCollection : ICollectionFixture<ClusterFixture>
    {
        public const string Name = "ClusterCollection";
    }
}
