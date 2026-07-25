using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Storage
{
    // Opt-in gate for tests that need Docker (currently just the Azurite-backed journal-storage
    // suite, work item #60 - Testcontainers self-provisions Azurite per run). xunit evaluates
    // Skip at discovery time, before any fixture or test body runs, so setting it here in the
    // constructor is what turns "no Docker" into "skipped" rather than "failed" for every test
    // using this attribute. Consults the SAME process-cached decision as
    // AzuriteClusterFixture.InitializeAsync - see DockerAvailability for why the decision must
    // be shared (its predecessor AzuriteProbe was process-cached for the identical reason: two
    // independent evaluations once disagreed and fail-opened into a bare
    // NullReferenceException, PR !16 build 47).
    public sealed class RequiresDockerFact : FactAttribute
    {
        public RequiresDockerFact()
        {
            if (!DockerAvailability.IsAvailable)
            {
                Skip = DockerAvailability.UnavailableReason;
            }
        }
    }
}
