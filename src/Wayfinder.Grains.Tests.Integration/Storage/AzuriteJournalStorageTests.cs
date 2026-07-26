using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using FluentAssertions;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Storage
{
    // Azurite restart-survival proof for work item #54: the journaled-grain storage path
    // (JournaledGrain + [LogConsistencyProvider("LogStorage")]) is backed by Azure Blob in
    // Development, not memory. See Wayfinder.Silo/Program.cs ConfigureDevelopmentOrleans for the
    // Orleans-source citation establishing which storage slot that grain shape actually
    // resolves, and AzuriteClusterFixture for the TestCluster wiring that mirrors it.
    //
    // Opt-in via RequiresDockerFact: skipped (not failed) when Docker isn't reachable, per the
    // constraint that this suite must never fail a machine with no Docker. Azurite itself is
    // self-provisioned by AzuriteClusterFixture via Testcontainers (work item #60) - no manual
    // docker compose step and no fixed port to collide with a developer's own Azurite.
    [Collection(AzuriteClusterCollection.Name)]
    public class AzuriteJournalStorageTests
    {
        private readonly IClusterClient _clusterClient;
        private readonly string _containerName;
        private readonly BlobServiceClient _blobServiceClient;

        public AzuriteJournalStorageTests(AzuriteClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;
            _containerName = fixture.ContainerName;
            _blobServiceClient = fixture.BlobServiceClient;
        }

        [RequiresDockerFact]
        public async Task GetState__Given_EventsRaisedThenGrainDeactivated__When_CalledAgain__Then_StateRehydratesFromJournal()
        {
            var grain = _clusterClient.GetGrain<IAzuriteJournalTestGrain>(Guid.NewGuid());

            await grain.Increment(3, "first");
            await grain.Increment(4, "second");

            var stateBeforeDeactivate = await grain.GetState();
            stateBeforeDeactivate.Counter.Should().Be(7);
            stateBeforeDeactivate.LastLabel.Should().Be("second");

            // DeactivateOnIdle forces the next call onto a fresh activation, which must
            // rehydrate state from the configured IGrainStorage (Azure Blob) rather than
            // finding it still resident in memory - this is the restart-survival gate. A fixed
            // sleep (or a timing-budget poll) here is a correctness risk, not just a flake risk:
            // if it were ever too short, the grain would still be resident and GetState() below
            // would read it WITHOUT the rehydrate-from-blob path this test exists to prove - a
            // silent false pass. ForceActivationCollection(TimeSpan.Zero) is the deterministic
            // replacement (issue #154 - same IManagementGrain the suite already uses for this in
            // TimerEventSchedulerGrainTests): DeactivateNow already requested deactivation via
            // DeactivateOnIdle, so the activation is idle, and ForceActivationCollection's Task
            // represents the collector sweep actually completing (its own doc comment: "a Task
            // representing the work performed"), not a fire-and-forget signal - so by the time it
            // returns, the idle activation is already gone, with no dependence on CPU
            // scheduling/contention the way a fixed sleep or a poll budget would have.
            await grain.DeactivateNow();
            var managementGrain = _clusterClient.GetGrain<IManagementGrain>(0);
            await managementGrain.ForceActivationCollection(TimeSpan.Zero);

            // Single, non-polling confirmation - not a timing budget - that the activation is
            // actually gone: if this ever failed it would mean the ForceActivationCollection
            // contract above doesn't hold, which is worth failing loud on rather than silently
            // reading a resident activation.
            (await managementGrain.GetActivationAddress(grain)).Should().BeNull(
                "the grain must have fully deactivated before the next call forces reactivation - " +
                "otherwise this test's rehydrate-from-blob proof would be reading a still-resident " +
                "in-memory activation instead");

            var stateAfterReactivate = await grain.GetState();

            stateAfterReactivate.Counter.Should().Be(7, "state must rehydrate from blob storage, not memory, after deactivation");
            stateAfterReactivate.LastLabel.Should().Be("second");
        }

        // Regression pin for the grain-storage serializer (work item #16, adapted for blob):
        // a serializer that cannot round-trip System.Text.Json.Nodes values does not FAIL the
        // write - the log-consistency LogViewAdaptor retries it forever and the grain call never
        // returns (observed on #16 as a 30s client timeout with the activation spinning). The
        // whole journal -> deactivate -> rehydrate cycle is therefore bounded so a regression
        // (e.g. losing the OrleansGrainStorageSerializer pin in AzuriteClusterFixture) fails
        // FAST with a diagnosis instead of hanging the run. JsonArray is the known-poisonous
        // shape per the #16 value-shape matrix; content equality via JsonNode.DeepEquals - never
        // assert on runtime JsonValue subtypes, which legitimately change across a round-trip.
        [RequiresDockerFact]
        public async Task GetState__Given_JsonNodeDocumentWithNestedArrayJournaled__When_DeactivatedAndCalledAgain__Then_DocumentRehydratesFromBlob()
        {
            var grain = _clusterClient.GetGrain<IAzuriteJournalTestGrain>(Guid.NewGuid());

            var document = new JsonObject
            {
                ["caseFile"] = "audit-2026-042",
                ["items"] = new JsonArray("evidence-1", 2, true),
                ["nested"] = new JsonObject { ["tags"] = new JsonArray("risk", "controls") }
            };

            var scenario = JournalDeactivateRehydrate();
            var winner = await Task.WhenAny(scenario, Task.Delay(TimeSpan.FromSeconds(20)));

            winner.Should().Be(scenario,
                "a hung journal write means the grain-storage serializer cannot round-trip JsonNode " +
                "state - the LogViewAdaptor retries the failed write forever (work item #16); check " +
                "the GrainStorageSerializer pin in AzuriteClusterFixture");

            var state = await scenario;

            JsonNode.DeepEquals(state.Document, document).Should().BeTrue(
                "the rehydrated document must equal the journaled document by content; rehydrated: " +
                $"{state.Document?.ToJsonString() ?? "<null>"}");

            async Task<AzuriteJournalTestState> JournalDeactivateRehydrate()
            {
                await grain.SetDocument(document);

                // See the deterministic-deactivation remarks on the other test above (issue
                // #154): DeactivateNow + ForceActivationCollection(TimeSpan.Zero) replaces a
                // timing-budget poll with the collector sweep's own completion signal.
                await grain.DeactivateNow();
                var managementGrain = _clusterClient.GetGrain<IManagementGrain>(0);
                await managementGrain.ForceActivationCollection(TimeSpan.Zero);

                (await managementGrain.GetActivationAddress(grain)).Should().BeNull(
                    "the grain must have fully deactivated before the next call forces reactivation - " +
                    "otherwise this test's rehydrate-from-blob proof would be reading a still-resident " +
                    "in-memory activation instead");

                return await grain.GetState();
            }
        }

        // Belt-and-braces: prove the journal actually lands in Azurite blob storage, not a
        // silent fallback to memory grain storage (which would make the test above pass for
        // the wrong reason - state would simply never have left the still-running silo process
        // if AddAzureBlobGrainStorageAsDefault were, say, shadowed by a stray
        // AddMemoryGrainStorageAsDefault registration). Uses the fixture's live BlobServiceClient
        // (dynamic connection string) rather than a hardcoded const - the container's host port
        // is assigned by Docker per run.
        [RequiresDockerFact]
        public async Task Increment__Given_EventRaised__Then_ContainerHoldsGrainBlob()
        {
            var grain = _clusterClient.GetGrain<IAzuriteJournalTestGrain>(Guid.NewGuid());

            await grain.Increment(5, "belt-and-braces");

            var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);

            var blobCount = 0;
            await foreach (var _ in containerClient.GetBlobsAsync())
            {
                blobCount++;
            }

            blobCount.Should().BeGreaterThan(0, "the grain's journal state should be persisted as a blob in the Azurite container, not held only in memory");
        }
    }
}
