using Xunit;

namespace Flow.Grains.Tests.Integration.Plan.CasePlanModel.RepetitionGuard
{
    [CollectionDefinition(RepetitionGuardClusterCollection.Name)]
    public class RepetitionGuardClusterCollection : ICollectionFixture<RepetitionGuardClusterFixture>
    {
        public const string Name = "RepetitionGuardClusterCollection";
    }
}
