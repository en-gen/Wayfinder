using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Plan.CasePlanModel.RepetitionGuard
{
    [CollectionDefinition(RepetitionGuardClusterCollection.Name)]
    public class RepetitionGuardClusterCollection : ICollectionFixture<RepetitionGuardClusterFixture>
    {
        public const string Name = "RepetitionGuardClusterCollection";
    }
}
