using System;
using System.Threading;
using Docker.DotNet;

namespace Wayfinder.Grains.Tests.Integration.Storage
{
    // Shared availability decision for Docker-gated suites (currently just the Azurite-backed
    // journal-storage suite, work item #60), evaluated exactly ONCE per test process and cached.
    //
    // Why process-cached (predecessor: AzuriteProbe, work item #54/#17 PR !16 build 47): the
    // original design probed independently at [Fact]-discovery time and again at fixture init.
    // On a cold hosted agent the two evaluations disagreed - discovery said reachable, fixture
    // init said unreachable - the fixture early-returned with no TestCluster while the gate had
    // already let the test through, and it failed with a bare NullReferenceException on the
    // first ClusterClient dereference. A single cached decision makes RequiresDockerFact and
    // AzuriteClusterFixture.InitializeAsync's no-op agree by construction; they must never be
    // allowed to consult independent evaluations again.
    //
    // Detection: Docker.DotNet.Enhanced (Testcontainers' own client library, pinned to the exact
    // version Testcontainers.Azurite pulls in transitively) via DockerClientBuilder, which
    // resolves the daemon endpoint with the same precedence as the docker CLI (DOCKER_HOST, then
    // the active Docker context, then the platform default socket/pipe) - the identical
    // resolution AzuriteContainer.StartAsync() performs internally, so this probe and the real
    // container start can never disagree about which daemon they mean. A single Ping is the
    // cheapest call that proves the daemon is actually answering (not merely that a socket file
    // or named pipe exists), which is why it is cheap enough to run at xunit discovery time.
    internal static class DockerAvailability
    {
        private static readonly TimeSpan PingBudget = TimeSpan.FromSeconds(3);

        private static readonly Lazy<bool> Available =
            new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

        public static bool IsAvailable => Available.Value;

        public const string UnavailableReason =
            "Docker is not reachable. The Azurite-backed suite self-provisions its own Azurite " +
            "container via Testcontainers, so it only needs Docker Desktop (Linux containers) " +
            "running - start it and re-run.";

        private static bool Probe()
        {
            try
            {
                using var client = new DockerClientBuilder().WithTimeout(PingBudget).Build();
                using var cts = new CancellationTokenSource(PingBudget);

                client.System.PingAsync(cts.Token).GetAwaiter().GetResult();

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
