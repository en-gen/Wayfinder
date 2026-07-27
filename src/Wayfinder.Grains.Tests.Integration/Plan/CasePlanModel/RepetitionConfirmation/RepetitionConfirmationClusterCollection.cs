using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Plan.CasePlanModel.RepetitionConfirmation
{
    [CollectionDefinition(RepetitionConfirmationClusterCollection.Name)]
    public class RepetitionConfirmationClusterCollection : ICollectionFixture<RepetitionConfirmationClusterFixture>
    {
        public const string Name = "RepetitionConfirmationClusterCollection";
    }
}
