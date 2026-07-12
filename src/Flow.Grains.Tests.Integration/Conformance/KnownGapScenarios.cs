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
    // ADO #21 - QUARANTINED conformance scenarios: spec rows the engine is KNOWN not to honor
    // today. Per this suite's honesty rule (Conformance/COVERAGE.md), these rows are never
    // omitted - each is an executable scenario whose body asserts the SPEC-mandated behavior and
    // whose Skip reason names the owning work item (or the #21 report finding, for gaps this
    // suite discovered that have no work item yet) plus the behavior actually observed when the
    // scenario was authored against develop @ f23a74b. Unskip a scenario when its work item
    // lands; a quarantined scenario that passes after unskipping closes its COVERAGE.md row to
    // Pinned.
    //
    // The quarantine protocol (work item #21): a conformance scenario that exposes an engine bug
    // NEVER fixes engine code and NEVER fails the suite - it skips, loudly, with the work item.
    //
    // Two engine findings DISCOVERED BY THIS SUITE (full repro detail in the #21 report; work
    // items to be filed) recur across these probes:
    //
    //  FINDING-1 (parent-to-child propagation stream-dead): children arm their parent-transition
    //  subscription on the parent's INSTANCE id (BaseBehavior.Activate ->
    //  SubscribeTo<PlanItemTransitionedEvent>(Host.ParentInstanceId, ...)), but every grain
    //  publishes its transitions on its DEFINITION id (CmmnElementGrain.PublishEvent ->
    //  GetCaseEventStream<TEvent>(Definition.Id); for CaseGrain that is the case-definition id,
    //  for a PlanItemGrain the PlanItem's model id). The two keys never match, so no
    //  parentSuspend/parentResume/parentTerminate event is ever delivered - every downward
    //  cascade of Tables 8.5/8.6/8.9 is dead on the wire, even though the child-side handlers
    //  (HandleParentTransitioned) are correctly implemented and unit-tested with hand-delivered
    //  events.
    //
    //  FINDING-2 (auto-start stage activation-context violation): an ordinary Stage reaching
    //  Active via the AUTO-start path - FALSE ManualActivationRule, so EnableOrStart fires Start
    //  while the Create trigger is still being processed and Stateless queues it - executes
    //  HandleEnterActiveFromStart's continuation on a non-activation thread; the first
    //  Host.GrainFactory access throws InvalidOperationException("Activation access violation. A
    //  non-activation thread attempted to access activation services.") from
    //  StageBehavior.CreateChild, the exception surfaces through the case's Trigger(Create), and
    //  no child is instantiated. The manual-start route (Enabled + a separate ManualStart grain
    //  call) is unaffected - LifecycleScenarios pins it green.
    //
    //  FINDING-3 (nested definition declarations unresolvable): the definition index built by
    //  CaseDefinitionGrain.Define keys definition-id paths (CPM.StageA.TaskA) while runtime
    //  grain scopes are instance-id paths (CPM.<instanceGuid>), so GetPlanItemDefinition's
    //  upward-walking lookup only ever matches definitions declared at the casePlanModel root -
    //  a definition declared inside a nested <stage> element (5.4.8's primary containment shape)
    //  throws "definition ... not registered" from PlanItemGrain.DefineRepetition when the
    //  nested stage tries to instantiate its children.
    [Collection(ClusterCollection.Name)]
    public class KnownGapScenarios
    {
        private readonly ConformanceHarness _harness;

        public KnownGapScenarios(ClusterFixture fixture)
        {
            _harness = new ConformanceHarness(fixture.ClusterClient);
        }

        // Table 8.12 (autoComplete=FALSE), Manual-Completion OR-branch, per the project's
        // adjudicated reading (deviation D4, docs/06 2.3): once all REQUIRED children are
        // terminal, manual completion must become available to the Case worker even while a
        // non-required child is still Active.
        //
        // PR !26 (work item #19, commit 39de993) fixed HALF of D4: an externally-invoked
        // Trigger(Complete) is now correctly gated by StageBehavior.
        // ManualCompletionCriteriaSatisfied, which for autoComplete=FALSE checks only "required
        // children terminal" (no no-Active-children conjunct) - confirmed by direct probe in this
        // exact scenario: calling stageGrain.Trigger(Complete) after the required child completes
        // now SUCCEEDS without throwing. But this scenario polls the OBSERVABILITY half - the
        // UserCompletable snapshot flag a Case worker's UI actually watches - and that half is
        // UNCHANGED: StageBehavior.HandleChildTransitioned still gates
        // Host.RaiseEvent(new UserCompletableCriteriaMet()) on
        // "childSnapshots.All(x => x.PlanItemState != PlanItemState.Active)", the same conflated
        // zero-Active-children condition the original gap named, so UserCompletable never flips
        // true while the non-required child stays Active. !26's D4 fix is INCOMPLETE: it opened
        // the gate but left the indicator that tells a caller the gate is open unlit.
        [Fact(Skip = "KnownGap: work item #19 (D4, Table 8.12) remainder - PR !26/commit 39de993 fixed the Trigger(Complete) enforcement gate (StageBehavior.ManualCompletionCriteriaSatisfied), confirmed by direct probe that stageGrain.Trigger(Complete) now succeeds without throwing in this exact scenario, but did NOT update the UserCompletable flag-raising condition in StageBehavior.HandleChildTransitioned, which still requires childSnapshots.All(state != Active) before raising UserCompletableCriteriaMet - the same conflated OR-branch condition as before #19. Observed on develop@cbd66d7: UserCompletable stays false after the required child completes while the non-required child is Active (10s poll timeout), even though Trigger(Complete) itself no longer throws. Needs a follow-up fix to HandleChildTransitioned's flag-raise condition; unskip when that lands.")]
        [ConformanceCitation("Table 8.12 / autoComplete=FALSE, Manual-Completion branch (D4 remainder)")]
        public async Task StageCompletion__Given_AutoCompleteFalseAndNonRequiredChildActive__Then_ManualCompletionBecomesAvailable()
        {
            var deployed = await _harness.DeployAndCreate("KnownGap_StageAutoCompleteFalseManualBlocked.cmmn");

            var stageGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemStageA", deployed.Scope);
            var stageAddress = _harness.ResolveChildAddress(
                deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemStageA", deployed.Scope);

            var stageActive = await stageGrain.Trigger(PlanItemTransition.ManualStart);
            stageActive.PlanItemState.Should().Be(PlanItemState.Active);

            var stageChildren = (Interfaces.Plan.PlanItem.Behaviors.StageBehaviorSnapshot)stageActive.BehaviorExtension;
            var requiredTaskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, stageChildren, "PlanItemRequired", stageAddress);
            var nonRequiredTaskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, stageChildren, "PlanItemNonRequired", stageAddress);

            var requiredSnapshot = await requiredTaskGrain.GetSnapshot();
            requiredSnapshot.PlanItemState.Should().Be(PlanItemState.Active);
            requiredSnapshot.Required.Should().BeTrue("8.6.3: the requiredRule evaluates TRUE on the create transition");
            (await nonRequiredTaskGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active);

            // The one required child completes; the non-required child deliberately stays Active.
            await requiredTaskGrain.Trigger(PlanItemTransition.Complete);

            var manualCompletionAvailable = await ConformanceHarness.PollUntil(
                async () => (await stageGrain.GetSnapshot()).UserCompletable);
            manualCompletionAvailable.Should().BeTrue(
                "Table 8.12 (autoComplete=FALSE, Manual-Completion branch): with all REQUIRED children terminal, a Case worker's manual completion must be available regardless of the non-required Active child (D4)");
        }

        // Table 8.8 (complete) + 8.6.4: "Stage and Task instances with a RepetitionRule that do
        // not have any entry criteria, will try to create a new instance every time an instance
        // transitions into the Complete or Terminate state. Under that condition the
        // RepetitionRule is re-evaluated and if the Expression evaluates to TRUE, a new instance
        // is created." SourceTask has RepetitionRule=TRUE and no entry criteria: completing its
        // only instance must spawn a second instance.
        //
        // PINNED by PR !26 (work item #19, D7, commit d991861): Stage/Task instances with a
        // RepetitionRule and no entry criteria now re-evaluate the rule on the complete/terminate
        // transitions (BaseBehavior.TryRepeatOnCompleteOrTerminate) and publish
        // PlanItemRepetitionCriteriaMetEvent when TRUE. Verified green on develop@cbd66d7 - no
        // longer a known gap.
        [Fact]
        [ConformanceCitation("8.6.4 / repeat-on-complete for no-entry-criteria items (D7)")]
        [ConformanceCitation("Table 8.8 / complete - RepetitionRule re-evaluation clause")]
        public async Task TaskRepetition__Given_RepetitionRuleAndNoEntryCriteria__Then_CompletionSpawnsNewInstance()
        {
            var deployed = await _harness.DeployAndCreate("KnownGap_TaskRepetitionNoEntryCriteria.cmmn");

            var sourceGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemSource", deployed.Scope);
            (await sourceGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active);

            await sourceGrain.Trigger(PlanItemTransition.Complete);

            var secondInstanceSpawned = await ConformanceHarness.PollUntil(async () =>
            {
                var caseSnapshot = await deployed.CaseGrain.GetSnapshot();
                return _harness.CountChildInstances(caseSnapshot.BehaviorExtension, "PlanItemSource") == 2;
            });

            secondInstanceSpawned.Should().BeTrue(
                "8.6.4: a no-entry-criteria item with a TRUE RepetitionRule must get a new instance when one completes (D7) - the owning Stage's child bookkeeping must show a second instance");
        }

        // Table 8.5 (Suspended): "A Case instance MUST propagate this state to its outermost
        // Stage instance. This state MUST then be propagated down to the outermost Stage
        // instance's contained EventListener, Milestone, Stage, and Task instances" - the
        // strongest propagation MUST in 8.4. The Task must transition via Table 8.8's parent
        // suspend row; it never receives the event (FINDING-1).
        [Fact]
        [ConformanceCitation("Table 8.5 / Suspended propagation MUST")]
        [ConformanceCitation("Table 8.6 / suspend - downward propagation")]
        [ConformanceCitation("Table 8.8 / parent suspend")]
        public async Task CaseSuspend__Given_ActiveChildTask__Then_SuspensionPropagatesToTask()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_CaseSuspendReactivateCascade.cmmn");

            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);
            (await taskGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active);

            var suspendedCase = await deployed.CaseGrain.Trigger(PlanItemTransition.Suspend);
            suspendedCase.PlanItemState.Should().Be(PlanItemState.Suspended);

            var taskSuspended = await ConformanceHarness.PollUntil(
                async () => (await taskGrain.GetSnapshot()).PlanItemState == PlanItemState.Suspended);
            taskSuspended.Should().BeTrue(
                "Table 8.5: the Case MUST propagate Suspended down to the outermost Stage instance's contained Task instances (Table 8.8 parent suspend)");
        }

        // Table 8.6 (terminate): "This state propagates down to the outermost Stage instance,
        // which in turn propagates it down to all its internal EventListener, Milestone, Stage,
        // and Task instances" - the Milestone must transition via Table 8.11's parent terminate
        // row (Available -> Terminated). It never receives the event (FINDING-1).
        [Fact]
        [ConformanceCitation("Table 8.6 / terminate - downward propagation")]
        [ConformanceCitation("Table 8.11 / parent terminate")]
        public async Task CaseTerminate__Given_AvailableChildMilestone__Then_TerminationPropagatesToMilestone()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_CaseTerminateReactivate.cmmn");

            var milestoneGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);
            (await milestoneGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available);

            var terminatedCase = await deployed.CaseGrain.Trigger(PlanItemTransition.Terminate);
            terminatedCase.PlanItemState.Should().Be(PlanItemState.Terminated);

            var milestoneTerminated = await ConformanceHarness.PollUntil(
                async () => (await milestoneGrain.GetSnapshot()).PlanItemState == PlanItemState.Terminated);
            milestoneTerminated.Should().BeTrue(
                "Table 8.6 (terminate) propagates down; Table 8.11 (parent terminate): Available -> Terminated when the parent stage terminates");
        }

        // Table 8.6 (re-activate) + Table 8.9's resume-propagation analog (note (2): children
        // return "to the state [they] had before the 'parent suspend'"): the case-level suspend
        // cascades Suspended down to the Task (Table 8.5 MUST), so leaving Suspended via
        // re-activate must release the children back to their pre-suspend states - otherwise the
        // case is Active while its entire plan stays frozen.
        //
        // PR !26 (work item #19, commit a13edd6) landed the CASE-level D8 remainder - Closed-state
        // immutability and CasePlanModelBehavior's own close/reactivate wiring - but, verified
        // against that commit's diff, it touches only CaseGrain.cs and CasePlanModelBehavior.cs:
        // no case-specific reactivate-to-children cascade was added, and per the commit's own
        // remarks re-activation deliberately still has NO entry action of its own. So this
        // scenario remains blocked at its very first precondition by FINDING-1 alone (untouched by
        // !26; owned by #63) - the task never observes Suspended in the first place, so the
        // reactivate-release half beneath it (children's HandleParentTransitioned recognizing only
        // resume/parentResume, never reactivate) stays unreachable and unexercised.
        [Fact]
        [ConformanceCitation("Table 8.6 / re-activate - child release (blocked by FINDING-1/#63; D8 case-level remainder resolved by !26)")]
        [ConformanceCitation("Table 8.9 / note (2) prior-state restoration")]
        public async Task CaseReactivate__Given_ChildrenSuspendedByCascade__Then_ChildrenReturnToPriorState()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_CaseSuspendReactivateCascade.cmmn");

            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);
            (await taskGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active);

            await deployed.CaseGrain.Trigger(PlanItemTransition.Suspend);

            var taskSuspended = await ConformanceHarness.PollUntil(
                async () => (await taskGrain.GetSnapshot()).PlanItemState == PlanItemState.Suspended);
            taskSuspended.Should().BeTrue("Table 8.5: the case-level suspend MUST cascade to the Task before release can be probed");

            var reactivatedCase = await deployed.CaseGrain.Trigger(PlanItemTransition.Reactivate);
            reactivatedCase.PlanItemState.Should().Be(PlanItemState.Active);

            var taskReleased = await ConformanceHarness.PollUntil(
                async () => (await taskGrain.GetSnapshot()).PlanItemState == PlanItemState.Active);
            taskReleased.Should().BeTrue(
                "Table 8.9 note (2) analog for the outermost Stage: leaving Suspended must return the cascaded-suspended child to its pre-suspend state (Active) - a reactivated Case whose entire plan stays frozen is not executing (D8)");
        }

        // Table 8.8 (parent suspend / parent resume) for a NESTED stage: suspending StageA must
        // propagate Suspended to its Active child TaskA, and resuming must return TaskA to its
        // pre-suspend state (Table 8.9 note (2)). The child-side handlers exist and the stage
        // publishes its transitions - but on the definition-id stream, while TaskA listens on the
        // instance-id stream (FINDING-1), so nothing arrives.
        [Fact]
        [ConformanceCitation("Table 8.8 / parent suspend, parent resume")]
        [ConformanceCitation("Table 8.9 / suspend and resume propagation rows")]
        public async Task StageSuspend__Given_NestedActiveTask__Then_TaskFollowsByPropagationOnly()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_NestedStageManualStart.cmmn");

            var stageGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemStageA", deployed.Scope);
            var stageAddress = _harness.ResolveChildAddress(
                deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemStageA", deployed.Scope);

            var stageActive = await stageGrain.Trigger(PlanItemTransition.ManualStart);
            stageActive.PlanItemState.Should().Be(PlanItemState.Active);

            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId,
                (Interfaces.Plan.PlanItem.Behaviors.StageBehaviorSnapshot)stageActive.BehaviorExtension,
                "PlanItemTaskA",
                stageAddress);
            (await taskGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active);

            (await stageGrain.Trigger(PlanItemTransition.Suspend)).PlanItemState.Should().Be(PlanItemState.Suspended);

            var taskSuspended = await ConformanceHarness.PollUntil(
                async () => (await taskGrain.GetSnapshot()).PlanItemState == PlanItemState.Suspended);
            taskSuspended.Should().BeTrue(
                "Table 8.7/8.8 (parent suspend): a suspending Stage MUST propagate Suspended to its contained Task instances");

            (await stageGrain.Trigger(PlanItemTransition.Resume)).PlanItemState.Should().Be(PlanItemState.Active);

            var taskResumed = await ConformanceHarness.PollUntil(
                async () => (await taskGrain.GetSnapshot()).PlanItemState == PlanItemState.Active);
            taskResumed.Should().BeTrue(
                "Table 8.8 (parent resume)/Table 8.9 note (2): the child must return to the state it had before the parent suspend - Active");
        }

        // Table 8.9 (exit rows): when a Stage terminates via its exit criterion, its children
        // transition via "exit" from any non-terminal state to Terminated. The Stage's own exit
        // is pinned green (SentryScenarios); the cascade to TaskA never happens (FINDING-1).
        [Fact]
        [ConformanceCitation("Table 8.9 / exit, terminate propagation rows")]
        [ConformanceCitation("Table 8.7 / Terminated propagation MUST")]
        public async Task StageExit__Given_NestedActiveTask__Then_ExitCascadesTerminationToTask()
        {
            var deployed = await _harness.DeployAndCreate("Sentry_ExitCriterionStage.cmmn");

            var stageGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemStageA", deployed.Scope);
            var stageAddress = _harness.ResolveChildAddress(
                deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemStageA", deployed.Scope);

            var stageActive = await stageGrain.Trigger(PlanItemTransition.ManualStart);
            stageActive.PlanItemState.Should().Be(PlanItemState.Active);

            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId,
                (Interfaces.Plan.PlanItem.Behaviors.StageBehaviorSnapshot)stageActive.BehaviorExtension,
                "PlanItemTaskA",
                stageAddress);
            (await taskGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active);

            var exitItem = _harness.CaseFileItem(deployed.CaseInstanceId, "ExitItem");
            await exitItem.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "ExitItem" }, JsonNode.Parse("""{"abort": false}"""));
            await exitItem.Update(JsonNode.Parse("""{"abort": true}"""));

            var stageTerminated = await ConformanceHarness.PollUntil(
                async () => (await stageGrain.GetSnapshot()).PlanItemState == PlanItemState.Terminated);
            stageTerminated.Should().BeTrue("the Stage's own exit is pinned green in SentryScenarios and must hold here too");

            var taskTerminated = await ConformanceHarness.PollUntil(
                async () => (await taskGrain.GetSnapshot()).PlanItemState == PlanItemState.Terminated);
            taskTerminated.Should().BeTrue(
                "Table 8.7/8.9: a terminating Stage MUST propagate Terminated to its contained Task instances (exit: Active -> Terminated)");
        }

        // Table 8.8 (start) for a nested STAGE + 8.7: with a FALSE ManualActivationRule, StageA
        // must auto-start to Active and instantiate TaskA - no Case-worker involvement. The
        // auto-start path crashes instead (FINDING-2): the queued Start trigger's entry actions
        // run on a non-activation thread and Host.GrainFactory throws, surfacing through the
        // case's Trigger(Create) call.
        [Fact(Skip = "KnownGap: FINDING-2 (#21 report, work item to be filed) - the auto-start path (FALSE ManualActivationRule -> queued Start trigger) executes StageBehavior.HandleEnterActiveFromStart on a non-activation thread. Observed on develop@f23a74b: InvalidOperationException 'Activation access violation. A non-activation thread attempted to access activation services.' from Host.GrainFactory in CreateChild (StageBehavior.cs:454), surfacing through ICaseGrain.Trigger(Create); no child instantiates. The manual-start route is pinned green in LifecycleScenarios.")]
        [ConformanceCitation("Table 8.8 / start (Stage) + 8.7 instantiation")]
        public async Task StageAutoStart__Given_ManualActivationRuleFalse__Then_StageActivatesAndInstantiatesChild()
        {
            var deployed = await _harness.DeployAndCreate("KnownGap_NestedStageAutoStart.cmmn");

            var stageGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemStageA", deployed.Scope);
            var stageAddress = _harness.ResolveChildAddress(
                deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemStageA", deployed.Scope);

            var stageSnapshot = await stageGrain.GetSnapshot();
            stageSnapshot.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.8 (start): a FALSE ManualActivationRule means the Stage auto-starts to Active");

            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId,
                (Interfaces.Plan.PlanItem.Behaviors.StageBehaviorSnapshot)stageSnapshot.BehaviorExtension,
                "PlanItemTaskA",
                stageAddress);
            (await taskGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active,
                "8.7: the auto-started Stage must instantiate its planned PlanItems on entry to Active");
        }

        // 8.6.4 (Figure 8.6): once a repetition is detected, "a new instance of the Task, Stage,
        // or Milestone is created and transitions to Available" - creation is the OWNING STAGE's
        // job (8.7: instances live in their Stage). SentryScenarios proves the sentry re-arm and
        // the milestone's repetition DETECTION (Repeated=true) work; this scenario asserts the
        // spawn itself: the CasePlanModel's child bookkeeping must show a second Milestone
        // instance (repetition 1).
        //
        // PINNED by PR !26 (Bug #62, commit 86d7b50): root cause was StageBehavior.
        // HandleChildRepeated's "instantiating repetition" log template declaring six
        // placeholders but passing five arguments - MEL's eager message-template renderer threw
        // FormatException inside a stream-delivery turn, and the streaming agent swallowed the
        // fault as a silent retry-then-drop, so the handler looked "never reached" though the
        // subscription wiring was correct the whole time. Verified green on develop@cbd66d7 - no
        // longer a known gap.
        [Fact]
        [ConformanceCitation("8.6.4 / repetition instance creation by the owning Stage (Bug #62)")]
        public async Task StageBookkeeping__Given_MilestoneRepetitionDetected__Then_StageSpawnsRepetitionInstance()
        {
            var deployed = await _harness.DeployAndCreate("Sentry_RearmRepetition.cmmn");

            var sourceGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "SourcePlanItem", deployed.Scope);
            var milestoneGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "MilestonePlanItem", deployed.Scope);

            // B completes -> milestone rep 0 occurs (proven green in SentryScenarios).
            await sourceGrain.Trigger(PlanItemTransition.ManualStart);
            await sourceGrain.Trigger(PlanItemTransition.Complete);
            (await ConformanceHarness.PollUntil(
                async () => (await milestoneGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed)).Should().BeTrue();

            // B' completes -> repetition detected (Repeated=true, proven green in SentryScenarios).
            var sourcePlanItemModel = deployed.CaseModel.CasePlanModel.PlanItems.Single(p => p.Id == "SourcePlanItem");
            var secondInstance = _harness.ClusterClient.GetGrain<IPlanItemInternalGrain>(
                deployed.CaseInstanceId, $"{deployed.Scope}.BookkeepingProbeB2");
            await secondInstance.Define(deployed.CaseDefinitionId, sourcePlanItemModel);
            await secondInstance.Trigger(PlanItemTransition.Create);
            await secondInstance.Trigger(PlanItemTransition.ManualStart);
            await secondInstance.Trigger(PlanItemTransition.Complete);

            (await ConformanceHarness.PollUntil(
                async () => (await milestoneGrain.GetSnapshot()).Repeated)).Should().BeTrue();

            // THE GAP: the detected repetition must materialize as a second instance in the
            // owning Stage's plan.
            var repetitionSpawned = await ConformanceHarness.PollUntil(async () =>
            {
                var caseSnapshot = await deployed.CaseGrain.GetSnapshot();
                return _harness.CountChildInstances(caseSnapshot.BehaviorExtension, "MilestonePlanItem") == 2;
            });

            repetitionSpawned.Should().BeTrue(
                "8.6.4/Figure 8.6: the detected repetition must yield a new Milestone instance in the owning Stage's plan - the stage's bookkeeping must show repetition 1 (Bug #62)");
        }

        // 5.4.8/Table 5.34: a Stage aggregates its own planItemDefinitions - declaring a task's
        // definition INSIDE a nested <stage> element is the spec's primary containment shape and
        // imports/lints cleanly. 8.7 then requires the nested stage, on becoming Active, to
        // instantiate its planned items from that definition. The engine's runtime definition
        // lookup cannot resolve it: the definition index keys definition-id paths
        // (CPM.StageA.TaskA) while runtime scopes are instance-id paths (CPM.<instanceGuid>), so
        // the upward-walking lookup (CaseDefinitionGrain.GetPlanItemDefinition) only ever finds
        // definitions declared at the casePlanModel root. Discovered by this suite; every other
        // nested-stage sample hoists definitions to the root (5.4.3 ancestor references) to route
        // around it.
        // Un-quarantined (#65): CaseDefinitionGrain.GetPlanItemDefinition now searches with
        // PlanItemGrain.DefineRepetition's parentDefinitionScope (threaded from
        // StageBehavior.CreateChild's Host.DefinitionScope) - a DEFINITION-id path - instead of
        // the runtime INSTANCE-id _scope, so nested-stage declarations resolve the same as
        // casePlanModel-root ones.
        [Fact]
        [ConformanceCitation("5.4.8 / nested planItemDefinitions + 8.7 instantiation (NEW discovery)")]
        public async Task NestedDeclaration__Given_DefinitionDeclaredInsideNestedStage__Then_NestedChildInstantiates()
        {
            var deployed = await _harness.DeployAndCreate("KnownGap_NestedDefinitionDeclaration.cmmn");

            var stageGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemStageA", deployed.Scope);
            var stageAddress = _harness.ResolveChildAddress(
                deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemStageA", deployed.Scope);

            var stageActive = await stageGrain.Trigger(PlanItemTransition.ManualStart);
            stageActive.PlanItemState.Should().Be(PlanItemState.Active,
                "8.7 requires StageA's planned items to instantiate on this entry to Active");

            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId,
                (Interfaces.Plan.PlanItem.Behaviors.StageBehaviorSnapshot)stageActive.BehaviorExtension,
                "PlanItemTaskA",
                stageAddress);

            // Enabled, not Active: TaskA declares no manualActivationRule, so Table 5.51's default
            // (TRUE) applies and it waits for a Case worker's ManualStart - same reasoning
            // LifecycleScenarios pins for StageA itself just above. The pre-#65 defect threw
            // InvalidOperationException before this state was ever reachable at all; landing here
            // (rather than stuck Uninitialized, or throwing) is what proves the nested-declared
            // TaskA definition resolved and its PlanItem instantiated (5.4.8 + 8.7).
            (await taskGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Enabled,
                "Table 5.51: TaskA declares no ManualActivationRule, so 8.7's instantiation leaves it waiting Enabled for a Case worker - reaching this state at all proves the nested-declared definition resolved (5.4.8)");
        }
    }
}
