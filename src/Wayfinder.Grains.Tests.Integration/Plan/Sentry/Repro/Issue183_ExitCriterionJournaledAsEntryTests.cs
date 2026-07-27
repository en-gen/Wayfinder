using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.PlanItem;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Tests.Integration.Conformance;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Xunit;
using CaseFileItemModel = Wayfinder.Grains.Interfaces.Model.CaseFileItem;

namespace Wayfinder.Grains.Tests.Integration.Plan.Sentry.Repro
{
    // REPRODUCTION for issue #183 (P2) - NOT a fix. Do not modify engine code from this file.
    // ~~~~~
    // Claim: StageBehavior.HandleSentrySatisfied / TaskBehavior.HandleSentrySatisfied raise
    // `EntryCriterionSatisfied` UNCONDITIONALLY, before branching on whether the satisfied
    // criterion is actually an EntryCriterion or an ExitCriterion:
    //
    //     Host.RaiseEvent(new EntryCriterionSatisfied { ... });
    //     if (criterion is EntryCriterion) { ... }
    //     else if (criterion is ExitCriterion && ...) { Host.RaiseEvent(new ExitCriterionSatisfied {...}); ... }
    //
    // PlanItemStore.Apply(EntryCriterionSatisfied) applies unconditionally to EntryCriterionStore
    // (Wayfinder.Grains/Plan/PlanItem/PlanItemStore.cs). So a PlanItem whose EXIT criterion fires
    // gets its EntryCriterionStore marked Satisfied too - even a PlanItem with NO entry criterion
    // declared at all, like TaskA in Sentry_ExitCriterionTask.cmmn below. Both events land in the
    // journal (this is what "corrupts the entry-criterion projection" means - not a transient
    // in-memory glitch but a real persisted event of the wrong TYPE), and because
    // ExitCriterionSatisfied is ALSO raised right after, TaskA's actual exit still functions
    // correctly - which is exactly why the claim says this is invisible in ordinary functional
    // testing and only surfaces in the journal or a projection built over EntryCriterionStore.
    //
    // Observability strategy: TaskA declares ZERO entry criteria in this model, so there is no
    // legitimate mechanism by which its EntryCriterionStore could ever read Satisfied - if it
    // does, that is unambiguous proof of the cross-contamination, not a false positive from some
    // other satisfied entry criterion. Checked two ways: (1) the live projection
    // (GetSnapshot().EntryCriterionStore) - what any projection/read-model consumer would see;
    // (2) the raw persisted journal (GetJournaledEvents(), the seam ADO #59 added specifically to
    // let a test read back confirmed events without reflection) - proving the wrong event TYPE is
    // what got durably recorded, which is what JournaledGrain replay will always reproduce on any
    // future rehydration (Apply() is a pure fold over exactly this event sequence - there is
    // nothing else for a rehydrated activation to derive EntryCriterionStore from).
    [Collection(ClusterCollection.Name)]
    public class Issue183_ExitCriterionJournaledAsEntryTests
    {
        private readonly ConformanceHarness _harness;

        public Issue183_ExitCriterionJournaledAsEntryTests(ClusterFixture fixture)
        {
            _harness = new ConformanceHarness(fixture.ClusterClient);
        }

        [Fact]
        public async Task Repro__Given_TaskWithOnlyExitCriterion__When_ExitCriterionSatisfied__Then_EntryCriterionStoreAndJournalStayClean()
        {
            var deployed = await _harness.DeployAndCreate("Sentry_ExitCriterionTask.cmmn");

            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);

            var beforeSnapshot = await taskGrain.GetSnapshot();
            beforeSnapshot.PlanItemState.Should().Be(PlanItemState.Active,
                "the Task must be executing before its exit criterion fires - 8.5 gates exit evaluation on Active");
            beforeSnapshot.EntryCriterionStore.State.Should().Be(CriterionState.Unsatisfied,
                "TaskA declares NO entry criterion at all in Sentry_ExitCriterionTask.cmmn - there is no " +
                "legitimate way its EntryCriterionStore could be anything but Unsatisfied before anything fires");

            var beforeEvents = await taskGrain.GetJournaledEvents();
            beforeEvents.OfType<EntryCriterionSatisfied>().Should().BeEmpty(
                "no entry criterion has been satisfied (or even declared) yet");

            // Drive the exit criterion: create ExitItem, then update it to abort=true. This is
            // exactly SentryScenarios.Sentry__Given_TaskExitCriterion__Then_CaseFileEventTerminatesActiveTask's
            // own driving sequence - the functional behavior (Task terminates) is not in question
            // here; #183 is entirely about what gets journaled/projected alongside it.
            var exitItem = _harness.CaseFileItem(deployed.CaseInstanceId, "ExitItem");
            await exitItem.Create(deployed.CaseDefinitionId, new CaseFileItemModel { Id = "ExitItem" }, JsonNode.Parse("""{"abort": false}"""));
            await exitItem.Update(JsonNode.Parse("""{"abort": true}"""));

            // Positive poll (30s, matching ConformanceHarness.DefaultTimeout / #149's CI-flake
            // precedent): waiting for the functional side-effect (Terminated) to settle before
            // reading the projection/journal, so the read below reflects the fully-processed
            // satisfaction rather than racing it.
            var terminated = await ConformanceHarness.PollUntil(
                async () => (await taskGrain.GetSnapshot()).PlanItemState == PlanItemState.Terminated);
            terminated.Should().BeTrue(
                "Table 8.8 (exit): the Task must transition Active -> Terminated when its exit criterion's " +
                "sentry is satisfied - this functional path is not what #183 disputes");

            var afterSnapshot = await taskGrain.GetSnapshot();

            // Sanity: the exit side DID work correctly (matches the issue's own observation that
            // this bug is invisible to functional testing because ExitCriterionSatisfied is also
            // raised correctly).
            afterSnapshot.ExitCriterionStore.State.Should().HaveFlag(CriterionState.Satisfied,
                "the exit criterion genuinely was satisfied and must be reflected in ExitCriterionStore");

            // THE KEY ASSERTION - fails today if #183's claim is correct: TaskA has no entry
            // criterion, so nothing should ever be able to mark EntryCriterionStore Satisfied.
            afterSnapshot.EntryCriterionStore.State.Should().Be(CriterionState.Unsatisfied,
                "TaskA declares no entry criterion - only its EXIT criterion fired, so EntryCriterionStore " +
                "must remain Unsatisfied. If this fails, HandleSentrySatisfied is raising EntryCriterionSatisfied " +
                "unconditionally (before branching on the criterion's actual type), and PlanItemStore is " +
                "applying it to EntryCriterionStore regardless of source - corrupting the entry-criterion " +
                "projection exactly as #183 describes.");

            // Journal-level proof: the wrong event TYPE must not even be PERSISTED, independent of
            // how the in-memory projection folds it - this is what makes it a journaling/replay
            // defect (any future rehydration folds over exactly this persisted sequence) rather
            // than a purely transient in-memory issue.
            var afterEvents = await taskGrain.GetJournaledEvents();
            afterEvents.OfType<ExitCriterionSatisfied>().Should().HaveCount(1,
                "exactly one ExitCriterion was satisfied exactly once");
            afterEvents.OfType<EntryCriterionSatisfied>().Should().BeEmpty(
                "TaskA has no entry criterion - no EntryCriterionSatisfied event should ever be journaled for it. " +
                "If this fails, the journal itself (not just the in-memory projection) contains an " +
                "EntryCriterionSatisfied event for what was actually an exit-criterion satisfaction - exactly " +
                "the corrupted audit/journal record #183 describes, and any replay/rehydration of this grain " +
                "from the journal will reproduce the same corrupted EntryCriterionStore every time.");
        }
    }
}
