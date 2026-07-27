using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.PlanItem;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Conformance
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

        // ADO #183 - Table 8.8 (exit) + 8.5: satisfying a Task's EXIT criterion must journal and
        // project ONLY ExitCriterionSatisfied. StageBehavior/TaskBehavior.HandleSentrySatisfied
        // used to raise Host.RaiseEvent(new EntryCriterionSatisfied {...}) UNCONDITIONALLY, before
        // branching on whether the satisfied criterion was actually an EntryCriterion or an
        // ExitCriterion; PlanItemStore.Apply(EntryCriterionSatisfied) then applied it to
        // EntryCriterionStore regardless of source. TaskA declares ZERO entry criteria in
        // Sentry_ExitCriterionTask.cmmn, so there is no legitimate mechanism by which its
        // EntryCriterionStore could ever read Satisfied - if it does, that is unambiguous proof of
        // the cross-contamination, not a false positive from some other satisfied entry criterion.
        // Checked two ways: (1) the live projection (GetSnapshot().EntryCriterionStore) - what any
        // projection/read-model consumer would see; (2) the raw persisted journal
        // (GetJournaledEvents(), the #59 seam) - proving the wrong event TYPE is what got durably
        // recorded, which is what JournaledGrain replay will always reproduce on any future
        // rehydration (Apply() is a pure fold over exactly this event sequence). Graduated from
        // its original quarantine as a one-off `Plan/Sentry/Repro/Issue183_*` reproduction into
        // this file per COVERAGE.md's honesty rule - it is an ordinary .cmmn-driven scenario
        // reusing Sentry_ExitCriterionTask.cmmn (the same sample the functional exit scenario
        // above already deploys), not a special case that belongs outside the suite's convention.
        [Fact]
        [ConformanceCitation("Table 8.8 / exit (Task) - journal/projection fidelity")]
        [ConformanceCitation("8.5 / sentry satisfaction must raise the event matching the criterion's type")]
        public async Task Sentry__Given_TaskExitCriterion__Then_EntryCriterionStoreAndJournalStayClean()
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

            var exitItem = _harness.CaseFileItem(deployed.CaseInstanceId, "ExitItem");
            await exitItem.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "ExitItem" }, JsonNode.Parse("""{"abort": false}"""));
            await exitItem.Update(JsonNode.Parse("""{"abort": true}"""));

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

            // THE KEY ASSERTION - fails without the #183 fix: TaskA has no entry criterion, so
            // nothing should ever be able to mark EntryCriterionStore Satisfied.
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

        // ADO #183 companion (positive path): Table 8.7 (entry) + 8.5 sentry semantics - a
        // GENUINE entry criterion satisfaction must still raise EntryCriterionSatisfied and mark
        // EntryCriterionStore Satisfied, both in the live projection and in the persisted
        // journal. #183's fix moved that RaiseEvent inside the `criterion is EntryCriterion`
        // branch of StageBehavior/TaskBehavior.HandleSentrySatisfied (previously unconditional,
        // which spuriously journaled it for EXIT criteria too - see the exit-side scenario
        // immediately above); this scenario pins that the entry side was not accidentally broken
        // by tightening that condition.
        [Fact]
        [ConformanceCitation("Table 8.7 / entry")]
        [ConformanceCitation("8.5 / sentry satisfaction drives EntryCriterionSatisfied")]
        public async Task Sentry__Given_TaskEntryCriterion__Then_CaseFileEventSatisfiesEntryAndJournalsEntryCriterionSatisfied()
        {
            var deployed = await _harness.DeployAndCreate("Sentry_EntryCriterionTask.cmmn");

            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);

            (await taskGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available,
                "TaskA declares an entry criterion, so it must wait in Available (8.7) until EntrySentry is satisfied");
            (await taskGrain.GetSnapshot()).EntryCriterionStore.State.Should().Be(CriterionState.Unsatisfied,
                "EntrySentry has not fired yet");

            var entryItem = _harness.CaseFileItem(deployed.CaseInstanceId, "EntryItem");
            await entryItem.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "EntryItem" }, JsonNode.Parse("""{"ready": false}"""));
            await entryItem.Update(JsonNode.Parse("""{"ready": true}"""));

            var active = await ConformanceHarness.PollUntil(
                async () => (await taskGrain.GetSnapshot()).PlanItemState == PlanItemState.Active);
            active.Should().BeTrue(
                "Table 8.7 (entry) + 8.6.2: the ManualActivationRule's condition evaluates FALSE (mirrors " +
                "Sentry_ExitCriterionTask.cmmn's own MAR_1), so satisfying the entry criterion transitions " +
                "TaskA straight from Available to Active via Start");

            var afterSnapshot = await taskGrain.GetSnapshot();
            afterSnapshot.EntryCriterionStore.State.Should().HaveFlag(CriterionState.Satisfied,
                "a genuine EntryCriterion satisfaction must still mark EntryCriterionStore Satisfied - the #183 " +
                "fix must not over-correct into dropping legitimate EntryCriterionSatisfied events");

            var afterEvents = await taskGrain.GetJournaledEvents();
            afterEvents.OfType<EntryCriterionSatisfied>().Should().HaveCount(1,
                "the entry criterion was satisfied exactly once, and the event journaled for it must be the " +
                "correctly-typed EntryCriterionSatisfied - not merely reflected in the in-memory projection");
            afterEvents.OfType<ExitCriterionSatisfied>().Should().BeEmpty(
                "TaskA declares no exit criterion at all - no ExitCriterionSatisfied should ever be journaled for it");
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

        // Table 8.6 (terminate) / 8.4.1 for the CasePlanModel ITSELF, as opposed to the sample
        // above's nested Stage: 8.4.1 prohibits only ENTRY criteria on the outermost Stage - its
        // own exit criteria are legal (tStage's XSD content model carries them as the Stage's own
        // <exitCriterion> children when it is the outermost Stage) and Table 8.6's terminate row
        // covers both a Case worker's decision and the CasePlanModel's own exit criteria becoming
        // satisfied - ONE trigger (`terminate`), two ways to reach it, not two triggers. No
        // Case-worker trigger is sent here - the case-file update alone must terminate the Case.
        // Pins ADO #66's fix, which had three independent facets: (1) CasePlanModelBehavior never
        // armed an ExitCriteria subscription at all (Create skips Available, the state where
        // StageBehavior normally arms it), (2) the satisfied-exit-criterion path had nothing wiring
        // it to the Table 8.6 `terminate` transition StateMachine.CanFire needed (originally
        // mis-fixed as a Permit(Exit) - Table 8.6 has NO `exit` row for the casePlanModel at all;
        // corrected to route through StageBehavior.ExitCriterionTransition, overridden in
        // CasePlanModelBehavior to Terminate, landing on the Permit(Terminate) edge that already
        // existed for the Case-worker-decision route - see PlanItemStateMachine.
        // ConfigureForCasePlanModel and CasePlanModelBehavior's remarks), and (3)
        // Host.Definition.ExitCriteria for the CasePlanModel's behavior host (CaseGrain) reads
        // Case.ExitCriteria - a fixed, always-empty collection wired in only to satisfy
        // IBehaviorDefinition - never CasePlanModel.ExitCriteria where a .cmmn's real criteria
        // land, so even an armed, permitted subscription would enumerate zero criteria without
        // CaseDefinitionGrain.Define's copy.
        [Fact]
        [ConformanceCitation("Table 8.6 / terminate (exit criteria)")]
        [ConformanceCitation("8.4.1 / CasePlanModel MAY declare exit criteria")]
        public async Task Sentry__Given_CasePlanModelExitCriterion__Then_CaseFileEventTerminatesCase()
        {
            var deployed = await _harness.DeployAndCreate("Sentry_ExitCriterionCasePlanModel.cmmn");

            (await deployed.CaseGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active,
                "the Case must be executing before its own exit criterion fires - 8.5 gates exit evaluation on Active");

            var taskAGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemTaskA", deployed.Scope);

            var exitItem = _harness.CaseFileItem(deployed.CaseInstanceId, "ExitItem");
            await exitItem.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "ExitItem" }, JsonNode.Parse("""{"abort": false}"""));
            await exitItem.Update(JsonNode.Parse("""{"abort": true}"""));

            var caseTerminated = await ConformanceHarness.PollUntil(
                async () => (await deployed.CaseGrain.GetSnapshot()).PlanItemState == PlanItemState.Terminated);
            caseTerminated.Should().BeTrue(
                "Table 8.6 (terminate via exit criteria): the Case must transition Active -> Terminated when the " +
                "CasePlanModel's own exit criterion's sentry is satisfied");

            // ADO #183: CasePlanModelBehavior shares StageBehavior.HandleSentrySatisfied verbatim
            // (no override - see CasePlanModelBehavior's own remarks), so the identical
            // spurious-EntryCriterionSatisfied corruption applied here too whenever a Case
            // terminated via its own exit criterion. 8.4.1 prohibits the CasePlanModel from
            // declaring entry criteria at all, so - exactly like TaskA in
            // Sentry_ExitCriterionTask.cmmn - there is no legitimate mechanism by which
            // CaseStore.EntryCriterionStore could ever read Satisfied; if it does, that is the same
            // cross-contamination #183 describes, at the CasePlanModel's own root.
            (await deployed.CaseGrain.GetSnapshot()).EntryCriterionStore.State.Should().Be(CriterionState.Unsatisfied,
                "the CasePlanModel cannot declare entry criteria (8.4.1) - only its own EXIT criterion fired, so " +
                "EntryCriterionStore must remain Unsatisfied");
            (await deployed.CaseGrain.GetJournaledEvents()).OfType<EntryCriterionSatisfied>().Should().BeEmpty(
                "no EntryCriterionSatisfied should ever be journaled for the CasePlanModel - its own exit " +
                "criterion firing must not also persist the wrong event type (#183)");

            // Table 8.9's mandated termination cascade: the CasePlanModel now reaches Terminated
            // via `terminate` rather than `exit` (see the class remarks above), and
            // StageBehavior/TaskBehavior.HandleParentTransitioned already treats a parent's
            // Terminate identically to a parent's Exit (both raise ParentTerminated and fire the
            // child's own Exit) - so TaskA must still cascade to Terminated regardless of which
            // trigger its parent fired. Locks that cascade instead of leaving it unasserted.
            var taskATerminated = await ConformanceHarness.PollUntil(
                async () => (await taskAGrain.GetSnapshot()).PlanItemState == PlanItemState.Terminated);
            taskATerminated.Should().BeTrue(
                "Table 8.9: the CasePlanModel's termination must cascade to its child TaskA, whether the " +
                "CasePlanModel itself reached Terminated via `terminate` or `exit`");
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

        // Table 5.30 (exit mode) + Bug #82 (D10): "the PlanItemOnPart of the Sentry occurs when
        // the PlanItem referenced by sourceRef transits by the specified exitCriterion due to the
        // Sentry that it refers to being satisfied" / "When sentryRef is specified, standardEvent
        // MUST have value 'exit.'" TaskA's own ExitCriterion_1 fires from a case-file event
        // (exactly like Sentry_ExitCriterionTask.cmmn); ListenerSentry's planItemOnPart names
        // TaskA + that same exitCriterionRef, so it must occur and complete ListenerMilestone.
        // Before this fix, PlanItemTransitionedEvent never carried a non-null ExitCriterionRef
        // (SentryGrain's D10 remarks), so this OnPart could never match - ListenerMilestone would
        // have stayed Available forever.
        [Fact]
        [ConformanceCitation("Table 5.30 / PlanItemOnPart exit mode (sentryRef + exitCriterionRef)")]
        public async Task Sentry__Given_PlanItemOnPartWithExitCriterionRef__Then_MatchesSpecificExitAndFiresListener()
        {
            var deployed = await _harness.DeployAndCreate("Sentry_ExitCriterionRefOnPart.cmmn");

            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);
            var listenerGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemListener", deployed.Scope);

            (await taskGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active,
                "the Task must be executing before its exit criterion fires - 8.5 gates exit evaluation on Active");
            (await listenerGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available,
                "ListenerMilestone waits on ListenerSentry, which has not yet occurred");

            var exitItem = _harness.CaseFileItem(deployed.CaseInstanceId, "ExitItem");
            await exitItem.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "ExitItem" }, JsonNode.Parse("""{"abort": false}"""));
            await exitItem.Update(JsonNode.Parse("""{"abort": true}"""));

            var taskTerminated = await ConformanceHarness.PollUntil(
                async () => (await taskGrain.GetSnapshot()).PlanItemState == PlanItemState.Terminated);
            taskTerminated.Should().BeTrue(
                "Table 8.8 (exit): TaskA must transition Active -> Terminated when ExitCriterion_1's sentry is satisfied");

            var listenerCompleted = await ConformanceHarness.PollUntil(
                async () => (await listenerGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed);
            listenerCompleted.Should().BeTrue(
                "Table 5.30 (exit mode): ListenerSentry's planItemOnPart names TaskA's own ExitCriterion_1 via " +
                "exitCriterionRef - TaskA exiting through THAT criterion must occur the OnPart, satisfy " +
                "ListenerSentry, and complete ListenerMilestone (Table 8.11 occur)");
        }

        // 5.4.4/Table 5.30 + Table 8.11 (occur): a PlanItem's entryCriteria is a collection - MORE
        // THAN ONE <entryCriterion> may be declared, each pointing at its own independent Sentry,
        // and "when ONE of the achieving Sentries (entry criteria) is satisfied" the PlanItem's
        // entry fires. Distinct from 8.5's AND-join (Sentry__Given_TwoOnParts__...), which is
        // about multiple OnParts inside ONE Sentry: here MilestoneA carries TWO independent
        // single-OnPart Sentries. Only ItemB is ever touched - ItemA's criterion never fires - and
        // the Milestone must still occur, proving satisfaction of any ONE criterion in the
        // collection is sufficient (OR semantics across entry criteria, not AND).
        [Fact]
        [ConformanceCitation("5.4.4 / Table 5.30 - PlanItem entryCriteria collection")]
        [ConformanceCitation("Table 8.11 / occur - one of the achieving Sentries")]
        public async Task Sentry__Given_TwoEntryCriteria__Then_EitherAloneSatisfiesEntry()
        {
            var deployed = await _harness.DeployAndCreate("Sentry_MultipleEntryCriteria.cmmn");

            var milestoneGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);
            (await milestoneGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available);

            var itemA = _harness.CaseFileItem(deployed.CaseInstanceId, "ItemA");
            var itemB = _harness.CaseFileItem(deployed.CaseInstanceId, "ItemB");
            await itemA.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "ItemA" }, JsonNode.Parse("""{"seen": false}"""));
            await itemB.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "ItemB" }, JsonNode.Parse("""{"seen": false}"""));

            // Only the SECOND criterion's source (ItemB) is ever updated - ItemA's own
            // EntryCriterion_1/SentryA never fires.
            await itemB.Update(JsonNode.Parse("""{"seen": true}"""));

            var completed = await ConformanceHarness.PollUntil(
                async () => (await milestoneGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed);
            completed.Should().BeTrue(
                "Table 8.11 (occur): satisfying EntryCriterion_2/SentryB alone is enough - a PlanItem " +
                "with multiple entry criteria needs only ONE of its achieving Sentries satisfied");
        }
    }
}
