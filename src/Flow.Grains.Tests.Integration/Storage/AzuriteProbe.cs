using System;
using System.Net.Sockets;
using System.Threading;

namespace Flow.Grains.Tests.Integration.Storage
{
    // Shared reachability decision for the Azurite suite (work item #54), evaluated exactly ONCE
    // per test process and cached.
    //
    // Why process-cached (PR !16 validation build 47): the original design probed independently
    // at [AzuriteFact] discovery time and again at fixture init. On the cold 2-core hosted agent
    // the discovery-time probe returned true (tests not skipped) while the fixture-init probe's
    // one-shot 500ms connect wait lost to process-start thread-pool/JIT contention and returned
    // false - the fixture early-returned with no TestCluster, and both tests failed with a bare
    // NullReferenceException on the first ClusterClient dereference. Two independent evaluations
    // fail OPEN when they disagree; a single cached decision makes gate and fixture consistent
    // by construction. The single evaluation is also hardened with retries, which a per-call
    // probe could not afford at discovery time.
    //
    // Skip gating alone is not enough: a collection fixture is constructed once per collection
    // regardless of whether any member test runs, so AzuriteClusterFixture must consult the same
    // decision to no-op instead of deploying a TestCluster against a machine with no Azurite -
    // the "must never fail a machine without Azurite running" constraint this suite satisfies.
    internal static class AzuriteProbe
    {
        public const string Host = "127.0.0.1";
        public const int BlobPort = 10000;

        private const int Attempts = 6;
        private static readonly TimeSpan ConnectBudget = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan Backoff = TimeSpan.FromMilliseconds(250);

        private static readonly Lazy<bool> Availability =
            new(ProbeWithRetries, LazyThreadSafetyMode.ExecutionAndPublication);

        public static bool IsAvailable => Availability.Value;

        public static readonly string UnreachableReason =
            $"Azurite is not reachable at {Host}:{BlobPort}. " +
            "Start it with: docker compose -f devops/infrastructure/docker-compose.yml up -d";

        private static bool ProbeWithRetries()
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                if (TryConnect())
                {
                    return true;
                }

                if (attempt < Attempts)
                {
                    Thread.Sleep(Backoff);
                }
            }

            return false;
        }

        private static bool TryConnect()
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(Host, BlobPort);
                return connectTask.Wait(ConnectBudget) && client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }
}
