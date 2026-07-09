using System;
using System.Net.Sockets;
using System.Threading;
using Azure;
using Azure.Storage.Blobs;

namespace Flow.Grains.Tests.Integration.Storage
{
    // Shared availability decision for the Azurite suite (work item #54), evaluated exactly ONCE
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
    // Why "available" is TCP + one blob service call, not TCP alone (observed 2026-07, while
    // working ADO #17): an Azurite running without --skipApiVersionCheck accepts the TCP connect
    // but rejects every SDK request with HTTP 400 InvalidHeaderValue ("The API version ... is
    // not supported by Azurite") whenever Azure.Storage.Blobs sends an x-ms-version newer than
    // that Azurite build knows (Azure/Azurite#2562 - the same gap the compose stack and CI close
    // by passing --skipApiVersionCheck). Under a TCP-only probe such a machine counted as
    // available, so AzuriteClusterFixture.InitializeAsync() threw during TestCluster.Deploy()
    // (the journaled-grain storage's container check at silo start) and xunit reported 3
    // FAILURES - violating the very constraint this gate exists to satisfy. The service call
    // uses the suite's own connection string, so "available" now means "the suite's blob calls
    // will actually be accepted", not merely "something is listening on 10000".
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

        // Budget for the single HTTP round-trip of the compatibility check, only ever spent
        // after a TCP connect has already succeeded. Generous relative to ConnectBudget because
        // a spurious timeout here silently SKIPS the suite on a machine where Azurite is fine -
        // a coverage loss nobody notices - while a machine with no Azurite never reaches it.
        private static readonly TimeSpan ServiceCallBudget = TimeSpan.FromSeconds(5);

        // Same connection shape as AzuriteClusterFixture and the suite's own assertions: the
        // probe must vouch for the exact pipeline the tests will use (well-known devstoreaccount1
        // dev credentials against Host:BlobPort).
        private const string AzuriteConnectionString = "UseDevelopmentStorage=true";

        private const string ComposeUpCommand =
            "docker compose -f devops/infrastructure/docker-compose.yml up -d";

        private static readonly Lazy<ProbeResult> Availability =
            new(ProbeWithRetries, LazyThreadSafetyMode.ExecutionAndPublication);

        public static bool IsAvailable => Availability.Value.IsAvailable;

        public static string UnreachableReason =>
            Availability.Value.UnreachableReason ?? NotReachableReason;

        private static readonly string NotReachableReason =
            $"Azurite is not reachable at {Host}:{BlobPort}. Start it with: {ComposeUpCommand}";

        // Internal (not private) so AzuriteProbeTests can pin that the skip reason names both
        // the cause and the two ways out; it is the only diagnostic a developer sees.
        internal static readonly string ApiVersionRejectedReason =
            $"Azurite at {Host}:{BlobPort} accepted the TCP connection but rejected the probe's " +
            "blob service call: it is running without --skipApiVersionCheck, and the " +
            "Azure.Storage.Blobs SDK sends an x-ms-version newer than it supports (HTTP 400 " +
            "InvalidHeaderValue, \"The API version ... is not supported by Azurite\"). Restart " +
            "Azurite with --skipApiVersionCheck, or use the repo stack which already sets it: " +
            ComposeUpCommand;

        private static string ServiceCallFailedReason(string lastFailure) =>
            $"Azurite at {Host}:{BlobPort} accepted the TCP connection but its blob service " +
            $"did not answer the probe call ({lastFailure}). The suite would fail against this " +
            $"endpoint, so it is skipped. Start a known-good Azurite with: {ComposeUpCommand}";

        // Azurite's version gate rejection (see ApiVersionRejectedReason). Matched on the two
        // stable message fragments plus the status code rather than the full sentence or the
        // error code alone: "InvalidHeaderValue" also covers genuinely malformed headers, and
        // Azurite's wording around the fragments has drifted across releases.
        internal static bool IsApiVersionRejection(RequestFailedException exception) =>
            exception.Status == 400
            && exception.Message.Contains("API version", StringComparison.OrdinalIgnoreCase)
            && exception.Message.Contains("not supported by Azurite", StringComparison.OrdinalIgnoreCase);

        private static ProbeResult ProbeWithRetries()
        {
            var connectedOnce = false;
            string lastServiceCallFailure = null;

            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                if (TryConnect())
                {
                    connectedOnce = true;

                    switch (TryBlobServiceCall(out var failureDetail))
                    {
                        case BlobServiceCallOutcome.Succeeded:
                            return ProbeResult.Available();

                        case BlobServiceCallOutcome.ApiVersionRejected:
                            // Terminal: the version gate is deterministic for the life of the
                            // Azurite process, so retrying cannot change the answer.
                            return ProbeResult.Unavailable(ApiVersionRejectedReason);

                        default:
                            lastServiceCallFailure = failureDetail;
                            break;
                    }
                }

                if (attempt < Attempts)
                {
                    Thread.Sleep(Backoff);
                }
            }

            return ProbeResult.Unavailable(
                connectedOnce ? ServiceCallFailedReason(lastServiceCallFailure) : NotReachableReason);
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

        // GetProperties is the cheapest authenticated read-only call that exercises the full
        // request pipeline (including Azurite's x-ms-version gate, which runs before auth); it
        // creates and mutates nothing. SDK retries are disabled because ProbeWithRetries owns
        // the retry policy - two nested retry loops would multiply the worst-case budget.
        private static BlobServiceCallOutcome TryBlobServiceCall(out string failureDetail)
        {
            try
            {
                var options = new BlobClientOptions();
                options.Retry.MaxRetries = 0;
                options.Retry.NetworkTimeout = ServiceCallBudget;

                new BlobServiceClient(AzuriteConnectionString, options).GetProperties();

                failureDetail = null;
                return BlobServiceCallOutcome.Succeeded;
            }
            catch (RequestFailedException exception) when (IsApiVersionRejection(exception))
            {
                failureDetail = FirstLine(exception.Message);
                return BlobServiceCallOutcome.ApiVersionRejected;
            }
            catch (Exception exception)
            {
                failureDetail = FirstLine(exception.Message);
                return BlobServiceCallOutcome.Failed;
            }
        }

        // RequestFailedException.Message spans many lines (status, headers, XML body); only the
        // first carries the diagnosis, and the skip reason must stay readable in a test summary.
        private static string FirstLine(string message)
        {
            var newline = message.IndexOfAny(new[] { '\r', '\n' });
            return newline < 0 ? message : message[..newline];
        }

        private enum BlobServiceCallOutcome
        {
            Succeeded,
            ApiVersionRejected,
            Failed
        }

        private readonly struct ProbeResult
        {
            private ProbeResult(bool isAvailable, string unreachableReason)
            {
                IsAvailable = isAvailable;
                UnreachableReason = unreachableReason;
            }

            public bool IsAvailable { get; }
            public string UnreachableReason { get; }

            public static ProbeResult Available() => new(true, null);
            public static ProbeResult Unavailable(string reason) => new(false, reason);
        }
    }
}
