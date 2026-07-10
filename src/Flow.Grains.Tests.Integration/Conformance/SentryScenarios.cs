using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Xunit;

namespace Flow.Grains.Tests.Integration.Conformance
{
    // ADO #21 - conformance scenarios for §8.5 Sentry semantics (satisfaction bullets, AND-join,
    // IfPart over the CaseFile, standalone IfPart, exit criteria) and 8.6.4's sentry-driven
    // repetition. All scenarios are .cmmn-file-driven; see Conformance/COVERAGE.md.
    [Collection(ClusterCollection.Name)]
    public class SentryScenarios
    {
        private readonly ConformanceHarness _harness;

        public SentryScenarios(ClusterFixture fixture)
        {
            _harness = new ConformanceHarness(fixture.ClusterClient);
        }

        // 8.5: "A Sentry is satisfied when ... All of the OnParts are satisfied AND there is no
        // IfPart" - ALL, not any: with two CaseFileItemOnParts, the first occurrence alone must
        // not satisfy the sentry; only once the second has also occurred may the Milestone occur
        // (Table 8.11's occur row: "when one of the achieving Sentries (entry criteria) is
        // satisfied").
        [Fact]
        [ConformanceCitation("8.5 / satisfaction bullet 2 (AND-join, no IfPart)")]
        [ConformanceCitation("Table 8.11 / occur")]
        public async Task Sentry__Given_TwoOnParts__Then_BothMustOccurBeforeSatisfaction()
        {
            var deployed = await _harness.DeployAndCreate("Sentry_AndJoin.cmmn");

            var milestoneGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);
            (await milestoneGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available);

            var itemA = _harness.CaseFileItem(deployed.CaseInstanceId, "ItemA");
            var itemB = _harness.CaseFileItem(deployed.CaseInstanceId, "ItemB");
            await itemA.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "ItemA" }, JsonNode.Parse("""{"seen": false}"""));
            await itemB.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "ItemB" }, JsonNode.Parse("""{"seen": false}"""));

            // First OnPart occurs (ItemA update) - the AND-join is incomplete, the sentry must
            // hold. (The two Create transitions above must not have counted either: the OnParts
            // are keyed to update, not create.)
            await itemA.Update(JsonNode.Parse("""{"seen": true}"""));

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            (await milestoneGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available,
                "8.5: ALL of the OnParts must be satisfied - one of two occurrences is not satisfaction");

            // Second OnPart occurs - AND-join complete, sentry satisfied, Milestone occurs.
            await itemB.Update(JsonNode.Parse("""{"seen": true}"""));

            var completed = await ConformanceHarness.PollUntil(
                async () => (await milestoneGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed);
            completed.Should().BeTrue(
                "8.5: with both OnParts occurred and no IfPart, the sentry is satisfied - Table 8.11 (occur): the Milestone completes");
        }

        // 8.5: "All of the OnParts are satisfied AND the IfPart condition evaluates to TRUE" -
        // the IfPart evaluates over the CaseFile (5.4.6.4 contextRef): an occurrence with the
        // condition FALSE must not satisfy; a later occurrence with it TRUE must. String-typed
        // condition, complementing the numeric conditions the D1/#20 flagship tests pin.
        [Fact]
        [ConformanceCitation("8.5 / satisfaction bullet 1 (OnParts + IfPart)")]
        [ConformanceCitation("5.4.6.4 / IfPart contextRef")]
        public async Task Sentry__Given_IfPartOverCaseFileContext__Then_OnlySatisfiedWhenConditionTrue()
        {
            var deployed = await _harness.DeployAndCreate("Sentry_IfPartContext.cmmn");

            var milestoneGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);

            var statusItem = _harness.CaseFileItem(deployed.CaseInstanceId, "StatusItem");
            await statusItem.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "StatusItem" }, JsonNode.Parse("""{"status": "pending"}"""));

            // OnPart occurs but the IfPart (value.status == 'approved') is FALSE - must hold.
            await statusItem.Update(JsonNode.Parse("""{"status": "rejected"}"""));

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            (await milestoneGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available,
                "8.5: the OnPart occurred but the IfPart evaluates FALSE over the CaseFile - the sentry must not be satisfied");

            // OnPart occurs again, IfPart now TRUE - satisfied.
            await statusItem.Update(JsonNode.Parse("""{"status": "approved"}"""));

            var completed = await ConformanceHarness.PollUntil(
                async () => (await milestoneGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed);
            completed.Should().BeTrue(
                "8.5: OnPart occurred AND IfPart TRUE (value.status == 'approved') - the sentry fires and the Milestone occurs");
        }

        // 8.5: "The IfPart condition evaluates to TRUE AND there are no OnParts" (satisfaction
        // bullet 3) and "Sentries with no OnPart must have an IfPart, and that IfPart will be
        // evaluated for all CaseFileItem events" (8.5's last sentence). Events on an UNRELATED
        // item (ItemB) must trigger evaluation without spuriously satisfying while the condition
        // is FALSE; once ItemA's own data makes the condition TRUE, the sentry fires.
        [Fact]
        [ConformanceCitation("8.5 / satisfaction bullet 3 (standalone IfPart)")]
        [ConformanceCitation("8.5 / evaluated for all CaseFileItem events")]
        public async Task Sentry__Given_StandaloneIfPart__Then_EvaluatesOnCaseFileEventsAndFiresWhenTrue()
        {
            var deployed = await _harness.DeployAndCreate("Sentry_StandaloneIfPart.cmmn");

            var milestoneGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);

            var itemA = _harness.CaseFileItem(deployed.CaseInstanceId, "ItemA");
            var itemB = _harness.CaseFileItem(deployed.CaseInstanceId, "ItemB");

            // ItemA exists with the condition (value.amount > 100) FALSE.
            await itemA.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "ItemA" }, JsonNode.Parse("""{"amount": 50}"""));

            // An event on a DIFFERENT CaseFileItem - 8.5's last sentence requires evaluation on
            // ALL CaseFileItem events; with the condition still FALSE it must not fire.
            await itemB.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "ItemB" }, JsonNode.Parse("""{"noise": true}"""));

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            (await milestoneGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available,
                "8.5: the standalone IfPart is evaluated on every CaseFileItem event, but amount=50 keeps it FALSE - no satisfaction");

            // ItemA's own update makes the condition TRUE - the very same case-wide evaluation
            // path must now satisfy the sentry.
            await itemA.Update(JsonNode.Parse("""{"amount": 150}"""));

            var completed = await ConformanceHarness.PollUntil(
                async () => (await milestoneGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed);
            completed.Should().BeTrue(
                "8.5 satisfaction bullet 3: the IfPart evaluates TRUE and there are no OnParts - the sentry is satisfied and the Milestone occurs");
        }

        // Table 8.8 (exit): "Transition when the exit criteria of the Stage or Task instance
        // becomes TRUE"; 8.5: "Exit criterion sentries are considered ready for evaluation while
        // the CasePlanModel, State, or Task is in Active state." No Case-worker trigger is sent -
        // the case-file update alone must terminate the Task.
        [Fact]
        [ConformanceCitation("Table 8.8 / exit (Task)")]
        [ConformanceCitation("8.5 / exit-criterion readiness while Active")]
        public async Task Sentry__Given_TaskExitCriterion__Then_CaseFileEventTerminatesActiveTask()
        {
            var deployed = await _harness.DeployAndCreate("Sentry_ExitCriterionTask.cmmn");

            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);
            (await taskGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active,
                "the Task must be executing before its exit criterion fires - 8.5 gates exit evaluation on Active");

            var exitItem = _harness.CaseFileItem(deployed.CaseInstanceId, "ExitItem");
            await exitItem.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "ExitItem" }, JsonNode.Parse("""{"abort": false}"""));
            await exitItem.Update(JsonNode.Parse("""{"abort": true}"""));

            var terminated = await ConformanceHarness.PollUntil(
                async () => (await taskGrain.GetSnapshot()).PlanItemState == PlanItemState.Terminated);
            terminated.Should().BeTrue(
                "Table 8.8 (exit): the Task must transition Active -> Terminated when its exit criterion's sentry is satisfied");
        }

        // Table 8.8 (exit) for a STAGE: StageA's exit criterion fires from a case-file event and
        // terminates the (manual-started) Active StageA - no Case-worker trigger. Pins the D6 fix
        // (stage exit criteria armed on the create path). Table 8.9's mandated termination
        // CASCADE to StageA's children is quarantined (KnownGapScenarios.StageExit__...:
        // parent-to-child propagation is stream-dead today).
        [Fact]
        [ConformanceCitation("Table 8.8 / exit (Stage)")]
        public async Task Sentry__Given_StageExitCriterion__Then_CaseFileEventTerminatesActiveStage()
        {
            var deployed = await _harness.DeployAndCreate("Sentry_ExitCriterionStage.cmmn");

            var stageGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemStageA", deployed.Scope);

            (await stageGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Enabled);
            var stageActive = await stageGrain.Trigger(PlanItemTransition.ManualStart);
            stageActive.PlanItemState.Should().Be(PlanItemState.Active,
                "the Stage must be executing before its exit criterion fires - 8.5 gates exit evaluation on Active");

            var exitItem = _harness.CaseFileItem(deployed.CaseInstanceId, "ExitItem");
            await exitItem.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "ExitItem" }, JsonNode.Parse("""{"abort": false}"""));
            await exitItem.Update(JsonNode.Parse("""{"abort": true}"""));

            var stageTerminated = await ConformanceHarness.PollUntil(
                async () => (await stageGrain.GetSnapshot()).PlanItemState == PlanItemState.Terminated);
            stageTerminated.Should().BeTrue(
                "Table 8.8 (exit): the Stage must transition Active -> Terminated when its exit criterion's sentry is satisfied");
        }

        // 8.6.4: "Stage and Task instances with a RepetitionRule will try to create a new
        // instance every time an entry criterion with an OnPart is satisfied" - presupposing
        // (8.5, Figure 8.5's B/B') that the SAME sentry satisfies once per distinct occurrence of
        // its OnPart's source. The model-instantiated source instance completes (sentry fires,
        // milestone rep 0 occurs), then a second, physically distinct instance sharing the same
        // PlanItem identity completes - the sentry must fire AGAIN (not stay latched from the
        // first satisfaction), reaching the milestone's repetition detection (Repeated flag).
        //
        // The second source instance is driven directly at a fresh grain address using the
        // IMPORTED PlanItem model object - exactly how StageBehavior.CreateChild reuses one
        // PlanItem across real repetitions, and the established precedent of
        // SentryRepetitionResetIntegrationTests (whose remarks document why stage-spawned
        // repetition instances cannot supply B' today - that spawn gap is Bug #62, quarantined
        // separately in KnownGapScenarios).
        [Fact]
        [ConformanceCitation("8.6.4 / repetition on entry criterion with OnPart")]
        [ConformanceCitation("8.5 / Figure 8.5 - one satisfaction per distinct source occurrence")]
        public async Task Sentry__Given_RepeatableMilestone__Then_SentryRearmsAcrossDistinctSourceInstances()
        {
            var deployed = await _harness.DeployAndCreate("Sentry_RearmRepetition.cmmn");

            var sourceGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "SourcePlanItem", deployed.Scope);
            var milestoneGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "MilestonePlanItem", deployed.Scope);

            (await milestoneGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available);

            // B: the model-instantiated source completes -> sentry satisfied -> milestone occurs.
            await sourceGrain.Trigger(PlanItemTransition.ManualStart);
            await sourceGrain.Trigger(PlanItemTransition.Complete);

            var milestoneCompleted = await ConformanceHarness.PollUntil(
                async () => (await milestoneGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed);
            milestoneCompleted.Should().BeTrue(
                "the first source completion satisfies RepeatSentry and the Milestone occurs (Table 8.11 occur)");

            (await milestoneGrain.GetSnapshot()).Repeated.Should().BeFalse(
                "8.6.4: the FIRST satisfaction is the milestone's own occurrence, not a repetition");

            // B': a second, distinct instance of the same PlanItem (the imported model object,
            // fresh grain address) completes - the same sentry must satisfy a second time.
            var sourcePlanItemModel = deployed.CaseModel.CasePlanModel.PlanItems.Single(p => p.Id == "SourcePlanItem");
            var secondInstance = _harness.ClusterClient.GetGrain<IPlanItemInternalGrain>(
                deployed.CaseInstanceId, $"{deployed.Scope}.RearmProbeB2");
            await secondInstance.Define(deployed.CaseDefinitionId, sourcePlanItemModel);
            await secondInstance.Trigger(PlanItemTransition.Create);
            await secondInstance.Trigger(PlanItemTransition.ManualStart);
            await secondInstance.Trigger(PlanItemTransition.Complete);

            var repeated = await ConformanceHarness.PollUntil(
                async () => (await milestoneGrain.GetSnapshot()).Repeated);
            repeated.Should().BeTrue(
                "8.6.4/Figure 8.5: the second, distinct source occurrence must satisfy the SAME sentry again - it re-arms per occurrence rather than latching Satisfied forever - reaching the milestone's repetition branch");
        }
    }
}
