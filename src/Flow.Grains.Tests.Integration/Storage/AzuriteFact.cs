using Xunit;

namespace Flow.Grains.Tests.Integration.Storage
{
    // Opt-in gate for the Azurite suite: the constraint is that this suite must never fail a
    // machine that simply isn't running Azurite (docker compose -f
    // devops/infrastructure/docker-compose.yml up -d). xunit evaluates Skip at discovery time,
    // before any fixture or test body runs, so setting it here in the constructor is what turns
    // a missing Azurite into "skipped" rather than "failed" for every test using this attribute.
    // Consults the SAME process-cached decision as AzuriteClusterFixture - see AzuriteProbe for
    // why the decision must be shared (PR !16 build 47: independent probes disagreed, fail-open
    // NullReferenceException).
    public sealed class AzuriteFact : FactAttribute
    {
        public AzuriteFact()
        {
            if (!AzuriteProbe.IsAvailable)
            {
                Skip = AzuriteProbe.UnreachableReason;
            }
        }
    }
}
