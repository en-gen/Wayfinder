using System;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Conformance
{
    // ADO #21 - conformance scenarios for the §8.4 lifecycle tables (8.5-8.11) and the Table 8.12
    // autoComplete=TRUE branch.
    // ~~~~~
    // Every scenario: a real .cmmn file (Conformance/Samples/Lifecycle_*.cmmn) imported, linted,
    // deployed, and driven through the public grain surface only (ICaseGrain / IPlanItemGrain
    // triggers). Each test names the spec table/row it pins in its ConformanceCitation metadata,
    // its comments, and its assertion messages. Spec-row -> scenario mapping is maintained in
    // Conformance/COVERAGE.md.
    [Collection(ClusterCollection.Name)]
    public class LifecycleScenarios
    {
        private readonly ConformanceHarness _harness;

        public LifecycleScenarios(ClusterFixture fixture)
        {
            _harness = new ConformanceHarness(fixture.ClusterClient);
        }

        // Table 8.6 (create): "The outermost Stage instance skips the Available state and MUST
        // transition directly to the Active state." Also pins the pre-trigger Uninitialized
        // baseline, so "skips Available" is actually observed rather than assumed.
        [Fact]
        [ConformanceCitation("Table 8.6 / create")]
        public async Task CaseCreate__Given_AnyCasePlanModel__Then_TransitionsDirectlyToActiveSkippingAvailable()
        {
            var deployed = await _harness.Deploy("Instantiation_MultiplePlanItems.cmmn");

            var beforeTrigger = await deployed.CaseGrain.GetSnapshot();
            beforeTrigger.PlanItemState.Should().Be(PlanItemState.Uninitialized,
                "before the create transition fires, the Case instance must not have advanced");

            var afterTrigger = await deployed.CaseGrain.Trigger(PlanItemTransition.Create);

            afterTrigger.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.6 (create): the outermost Stage instance skips Available and MUST transition directly to Active");
        }

        // Table 5.51: "If no ManualActivationRule is specified, then the default is considered
        // TRUE." Table 8.8 (enable): Available -> Enabled when the ManualActivationRule is TRUE;
        // (manual start): Enabled -> Active by Case worker decision; (complete): Active ->
        // Completed. Then Table 8.12 (autoComplete=TRUE): with no Active children and no required
        // children outstanding, the parent Stage - here the CasePlanModel itself - completes,
        // which is Table 8.6's complete row observed at the Case level.
        [Fact]
        [ConformanceCitation("Table 5.51 / ManualActivationRule default TRUE")]
        [ConformanceCitation("Table 8.8 / enable, manual start, complete")]
        [ConformanceCitation("Table 8.12 / autoComplete=TRUE")]
        [ConformanceCitation("Table 8.6 / complete")]
        public async Task TaskLifecycle__Given_NoManualActivationRule__Then_EnabledThenManualStartThenCompleteCompletesCase()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_ManualActivationDefault.cmmn");

            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);

            var created = await taskGrain.GetSnapshot();
            created.PlanItemState.Should().Be(PlanItemState.Enabled,
                "Table 5.51: an absent ManualActivationRule is considered TRUE, so per Table 8.8 (enable) the Task must sit in Enabled awaiting a Case worker");

            var active = await taskGrain.Trigger(PlanItemTransition.ManualStart);
            active.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.8 (manual start): Enabled -> Active by Case worker decision");

            var completed = await taskGrain.Trigger(PlanItemTransition.Complete);
            completed.PlanItemState.Should().Be(PlanItemState.Completed,
                "Table 8.8 (complete): Active -> Completed when the Task's purpose is accomplished");

            var caseCompleted = await ConformanceHarness.PollUntil(
                async () => (await deployed.CaseGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed);
            caseCompleted.Should().BeTrue(
                "Table 8.12 (autoComplete=TRUE): no Active children and all required children terminal - the CasePlanModel must complete (Table 8.6 complete)");
        }

        // 8.6.2: "If this rule evaluates to TRUE, the Task or Stage instance transitions from
        // Available to Enabled, otherwise it transitions from Available to Active" - the
        // otherwise-branch, Table 8.8's start row: no Case worker involvement.
        [Fact]
        [ConformanceCitation("8.6.2 / ManualActivationRule FALSE")]
        [ConformanceCitation("Table 8.8 / start")]
        public async Task TaskLifecycle__Given_ManualActivationRuleFalse__Then_StartsDirectlyToActive()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_ManualActivationExplicitFalse.cmmn");

            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);

            var snapshot = await taskGrain.GetSnapshot();
            snapshot.PlanItemState.Should().Be(PlanItemState.Active,
                "8.6.2/Table 8.8 (start): a FALSE ManualActivationRule means Available -> Active directly, never pausing in Enabled");
        }

        // Table 8.8 (disabled): Enabled -> Disabled by Case worker decision; (re-enable):
        // Disabled -> Enabled by Case worker decision - the full decline-then-reconsider loop,
        // finishing with manual start into Active to prove the loop leaves the Task fully usable.
        [Fact]
        [ConformanceCitation("Table 8.8 / disabled, re-enable")]
        public async Task TaskLifecycle__Given_EnabledTask__Then_DisableAndReenableRoundTrips()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_TaskDisableReenable.cmmn");

            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);

            (await taskGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Enabled,
                "Table 5.51 default TRUE puts the Task in Enabled first");

            var disabled = await taskGrain.Trigger(PlanItemTransition.Disable);
            disabled.PlanItemState.Should().Be(PlanItemState.Disabled,
                "Table 8.8 (disabled): Enabled -> Disabled by Case worker decision");

            var reenabled = await taskGrain.Trigger(PlanItemTransition.Reenable);
            reenabled.PlanItemState.Should().Be(PlanItemState.Enabled,
                "Table 8.8 (re-enable): Disabled -> Enabled by Case worker decision");

            var active = await taskGrain.Trigger(PlanItemTransition.ManualStart);
            active.PlanItemState.Should().Be(PlanItemState.Active,
                "after re-enable the Task must be startable exactly as if never disabled");
        }

        // Table 5.39: "If isBlocking is set to FALSE, the Task is not waiting for the work to
        // complete and completes immediately, upon instantiation" - no Complete trigger is ever
        // sent by this scenario; the engine must complete the Task by itself once Active.
        [Fact]
        [ConformanceCitation("Table 5.39 / isBlocking=FALSE")]
        public async Task TaskLifecycle__Given_NonBlockingTask__Then_CompletesOnItsOwn()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_NonBlockingTaskAutoCompletes.cmmn");

            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);

            var completed = await ConformanceHarness.PollUntil(
                async () => (await taskGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed);

            completed.Should().BeTrue(
                "Table 5.39: a non-blocking Task completes immediately upon activation, with no external Complete trigger");
        }

        // Table 8.8 (fault): Active -> Failed, "This state MUST NOT propagate"; (re-activated):
        // Failed -> Active "when the source of the failure has been resolved"; then (terminate):
        // Active -> Terminated by Case worker decision. The parent Case must still be Active at
        // the end - the fault MUST NOT have propagated to it.
        [Fact]
        [ConformanceCitation("Table 8.8 / fault, re-activated, terminate")]
        public async Task TaskLifecycle__Given_ActiveTask__Then_FaultReactivateTerminateWalkTable88()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_TaskFaultReactivate.cmmn");

            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);

            (await taskGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active);

            var failed = await taskGrain.Trigger(PlanItemTransition.Fault);
            failed.PlanItemState.Should().Be(PlanItemState.Failed,
                "Table 8.8 (fault): Active -> Failed on exception or software failure");

            var reactivated = await taskGrain.Trigger(PlanItemTransition.Reactivate);
            reactivated.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.8 (re-activated): Failed -> Active once the failure is resolved");

            var terminated = await taskGrain.Trigger(PlanItemTransition.Terminate);
            terminated.PlanItemState.Should().Be(PlanItemState.Terminated,
                "Table 8.8 (terminate): Active -> Terminated by Case worker decision");

            (await deployed.CaseGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.8 (fault): 'This state MUST NOT propagate' - the Case must have stayed Active throughout");
        }

        // Table 8.11 (create): Milestone -> Available; (suspend): Available -> Suspended by Case
        // worker decision; (resume): Suspended -> Available; (terminate): Available -> Terminated
        // by Case worker decision. The full Table 8.10/8.11 walk for a Milestone that is never
        // achieved.
        [Fact]
        [ConformanceCitation("Table 8.11 / create, suspend, resume, terminate")]
        public async Task MilestoneLifecycle__Given_UnachievedMilestone__Then_SuspendResumeTerminateWalkTable811()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_MilestoneLifecycle.cmmn");

            var milestoneGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);

            (await milestoneGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available,
                "Table 8.11 (create): a Milestone instance starts in Available, waiting on its entry criterion");

            var suspended = await milestoneGrain.Trigger(PlanItemTransition.Suspend);
            suspended.PlanItemState.Should().Be(PlanItemState.Suspended,
                "Table 8.11 (suspend): Available -> Suspended by Case worker decision");

            var resumed = await milestoneGrain.Trigger(PlanItemTransition.Resume);
            resumed.PlanItemState.Should().Be(PlanItemState.Available,
                "Table 8.11 (resume): Suspended -> Available - back to waiting, not to any other state");

            var terminated = await milestoneGrain.Trigger(PlanItemTransition.Terminate);
            terminated.PlanItemState.Should().Be(PlanItemState.Terminated,
                "Table 8.11 (terminate): Available -> Terminated by Case worker decision");
        }

        // Table 8.8 (manual start) for a STAGE: Enabled -> Active by Case worker decision, and
        // 8.7: entering Active instantiates the Stage's planned PlanItems - TaskA must exist and
        // (FALSE ManualActivationRule) auto-start inside it. Also pins Table 8.8 (suspended /
        // resume) for the Stage instance itself. What the suspend does to the CHILD - Table
        // 8.8's parent suspend/parent resume rows - is quarantined (KnownGapScenarios:
        // parent-to-child propagation is stream-dead today; the auto-start route into Active is
        // separately broken - KnownGap_NestedStageAutoStart).
        [Fact]
        [ConformanceCitation("Table 8.8 / manual start (Stage), suspended, resume")]
        [ConformanceCitation("8.7 / nested-stage instantiation on Active")]
        public async Task StageLifecycle__Given_EnabledNestedStage__Then_ManualStartInstantiatesChildrenAndSuspendResumeWorks()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_NestedStageManualStart.cmmn");

            var stageGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemStageA", deployed.Scope);
            var stageAddress = _harness.ResolveChildAddress(
                deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemStageA", deployed.Scope);

            (await stageGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Enabled,
                "Table 5.51: StageA declares no ManualActivationRule, so it waits Enabled for a Case worker");

            var stageActive = await stageGrain.Trigger(PlanItemTransition.ManualStart);
            stageActive.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.8 (manual start): Enabled -> Active by Case worker decision");

            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId,
                (Interfaces.Plan.PlanItem.Behaviors.StageBehaviorSnapshot)stageActive.BehaviorExtension,
                "PlanItemTaskA",
                stageAddress);

            (await taskGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active,
                "8.7: the Stage's planned PlanItems instantiate on its entry to Active, and TaskA's FALSE ManualActivationRule auto-starts it");

            var stageSuspended = await stageGrain.Trigger(PlanItemTransition.Suspend);
            stageSuspended.PlanItemState.Should().Be(PlanItemState.Suspended,
                "Table 8.8 (suspended): Active -> Suspended by Case worker decision");

            var stageResumed = await stageGrain.Trigger(PlanItemTransition.Resume);
            stageResumed.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.8 (resume): Suspended -> Active by Case worker decision");
        }

        // Table 8.6 (terminate): Active -> Terminated by Case worker decision; (re-activate):
        // Terminated -> Active by a Case worker or administrator - the CASE instance's own rows.
        // Table 8.6's downward propagation of the terminate ("propagates it down to all its
        // internal ... instances", Table 8.11 parent terminate) is quarantined
        // (KnownGapScenarios.CaseTerminate__...: parent-to-child propagation is stream-dead).
        [Fact]
        [ConformanceCitation("Table 8.6 / terminate, re-activate")]
        public async Task CaseLifecycle__Given_ActiveCase__Then_TerminateAndReactivateWalkTable86()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_CaseTerminateReactivate.cmmn");

            var terminatedCase = await deployed.CaseGrain.Trigger(PlanItemTransition.Terminate);
            terminatedCase.PlanItemState.Should().Be(PlanItemState.Terminated,
                "Table 8.6 (terminate): Active -> Terminated by Case worker decision");

            var reactivatedCase = await deployed.CaseGrain.Trigger(PlanItemTransition.Reactivate);
            reactivatedCase.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.6 (re-activate): Terminated -> Active by a Case worker or administrator");
        }

        // Table 8.6 (suspend): Active -> Suspended by Case worker decision; (re-activate):
        // Suspended -> Active - the CASE instance's own rows. Table 8.5's mandated downward
        // propagation of the Suspended state, and the release of children on re-activate, are
        // both quarantined (KnownGapScenarios.CaseSuspend__/CaseReactivate__...: parent-to-child
        // propagation is stream-dead; work item #19/D8 owns the reactivate-release semantics).
        [Fact]
        [ConformanceCitation("Table 8.6 / suspend, re-activate")]
        public async Task CaseLifecycle__Given_ActiveCase__Then_SuspendAndReactivateWalkTable86()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_CaseSuspendReactivateCascade.cmmn");

            var suspendedCase = await deployed.CaseGrain.Trigger(PlanItemTransition.Suspend);
            suspendedCase.PlanItemState.Should().Be(PlanItemState.Suspended,
                "Table 8.6 (suspend): Active -> Suspended by Case worker decision");

            var reactivatedCase = await deployed.CaseGrain.Trigger(PlanItemTransition.Reactivate);
            reactivatedCase.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.6 (re-activate): Suspended -> Active by a Case worker or administrator");
        }

        // Table 8.6 (close): {Completed, Terminated, Failed, Suspended} -> Closed "when no
        // further work or modifications should be allowed"; Table 8.4/8.5: Closed is a TERMINAL
        // state - re-activate is defined from {Completed, Terminated, Failed, Suspended} only,
        // never from Closed. Per PR !26 (work item #19, D8 remainder, CaseGrain.cs): a reactivation
        // attempt on a Closed Case is now rejected LOUDLY - CaseGrain.Trigger throws
        // InvalidOperationException at the public surface - rather than the previous silent
        // unhandled-trigger no-op, so the Case both throws and never leaves Closed.
        [Fact]
        [ConformanceCitation("Table 8.6 / close")]
        [ConformanceCitation("Table 8.5 / Closed is terminal")]
        public async Task CaseLifecycle__Given_CompletedCase__Then_ClosesAndStaysClosedAgainstReactivation()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_NonBlockingTaskAutoCompletes.cmmn");

            var caseCompleted = await ConformanceHarness.PollUntil(
                async () => (await deployed.CaseGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed);
            caseCompleted.Should().BeTrue(
                "the non-blocking Task self-completes and autoComplete=TRUE completes the Case (Table 8.12) - the precondition for close");

            var closedCase = await deployed.CaseGrain.Trigger(PlanItemTransition.Close);
            closedCase.PlanItemState.Should().Be(PlanItemState.Closed,
                "Table 8.6 (close): Completed -> Closed when no further work should be allowed");

            await deployed.CaseGrain
                .Awaiting(x => x.Trigger(PlanItemTransition.Reactivate))
                .Should()
                .ThrowAsync<InvalidOperationException>(
                    "Table 8.5/8.6: Closed is a terminal state and re-activate is not defined from it - #19/D8 remainder makes the rejection loud instead of a silent no-op");

            (await deployed.CaseGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Closed,
                "the rejected reactivation attempt must not move the Case off Closed");
        }

        // Table 8.6 (fault): Active -> Failed "when the outermost Stage instance reaches the
        // Failed state"; (re-activate): Failed -> Active - the Case-level failure excursion.
        [Fact]
        [ConformanceCitation("Table 8.6 / fault, re-activate")]
        public async Task CaseLifecycle__Given_ActiveCase__Then_FaultReachesFailedAndReactivateRecovers()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_CaseTerminateReactivate.cmmn");

            var failedCase = await deployed.CaseGrain.Trigger(PlanItemTransition.Fault);
            failedCase.PlanItemState.Should().Be(PlanItemState.Failed,
                "Table 8.6 (fault): Active -> Failed on exception or software failure");

            var reactivatedCase = await deployed.CaseGrain.Trigger(PlanItemTransition.Reactivate);
            reactivatedCase.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.6 (re-activate): Failed -> Active once the failure is resolved");
        }

        // Table 8.12, autoComplete=TRUE column: "There are no Active children, AND all required
        // children are in {Disabled, Completed, Terminated, Failed}." Both conjuncts are satisfied
        // VACUOUSLY over the empty set for a Stage with no <planItem> children and no
        // <planningTable> at all - StageBehavior.HandleChildTransitioned evaluated this exclusively
        // in reaction to a child's own terminal transition, so a childless Stage (which produces
        // zero such transitions) sat Active forever (#180). Fixed by StageBehavior.
        // HandleEnterActiveFromStart evaluating the same shared predicate once, inline, right after
        // its (here empty) child fan-out completes.
        //
        // Graduated from a standalone repro test (originally
        // Plan/CasePlanModel/Repro/Issue180_EmptyStageCompletionIntegrationTests.cs) into this
        // suite once the shape was confirmed to round-trip through CmmnXmlSerializer/
        // CmmnCapabilityLint/ToDeployableCase like any other sample - an empty <stage> element is
        // ordinary CMMN XML, nothing about it needed special-casing.
        [Fact]
        [ConformanceCitation("Table 8.12 / autoComplete=TRUE, vacuous zero-children satisfaction (#180)")]
        public async Task StageCompletion__Given_AutoCompleteStageWithZeroPlanItems__Then_CompletesVacuously()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_EmptyStageAutoCompletesVacuously.cmmn");

            var stageGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemStageEmpty", deployed.Scope);

            (await stageGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Enabled,
                "Table 5.51: StageEmpty declares no ManualActivationRule, so it waits Enabled for a Case worker");

            var stageSnapshot = await stageGrain.Trigger(PlanItemTransition.ManualStart);

            // Deliberately BeOneOf(Active, Completed), not a hard Completed assertion: with the
            // current fix, PlanItemGrain.Trigger awaits the behavior's ENTIRE transition cascade
            // (Stateless chains every OnEntryFromAsync synchronously) before taking the snapshot it
            // returns, so a genuinely childless Stage's vacuous completion happens to be observable
            // INLINE, in this same call - but that inline timing is an implementation detail of
            // today's fix (HandleEnterActiveFromStart evaluating synchronously), not something
            // Table 8.12 itself mandates. A correct engine that instead completed via a
            // self-published follow-up event would still be correct, and must not fail this
            // assertion; the PollUntil below is the real, timing-independent proof.
            stageSnapshot.PlanItemState.Should().BeOneOf(PlanItemState.Active, PlanItemState.Completed);

            var completed = await ConformanceHarness.PollUntil(
                async () => (await stageGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed);

            completed.Should().BeTrue(
                "Table 8.12 autoComplete=TRUE is satisfied vacuously by a Stage with zero children - " +
                "if completion were only ever evaluated from inside HandleChildTransitioned (issue #180), " +
                "a Stage that creates no children could never trigger that evaluation and would be wedged Active forever");
        }

        // Table 8.12, autoComplete=FALSE column: requires EXPLICIT completion (the Manual
        // Completion OR-branch, StageBehavior.ManualCompletionCriteriaSatisfied/Trigger's
        // override) - unlike the autoComplete=TRUE case above, a Stage in this mode must NOT
        // complete itself just because it happens to have zero children. The #180 fix gates its
        // new inline completion check on PlanItemDefinition.AutoComplete specifically so this
        // shape is untouched; this scenario pins that the gate holds in both directions, not just
        // the one the original bug report exercised.
        //
        // Deliberately NOT a sleep-then-assert-it-hasn't-happened test (a fixed window is silently
        // permissive: it can pass merely because nothing had run yet, proving nothing about
        // whether it ever WOULD run). No timing window is needed at all here: the fix gates its
        // eager completion check on AutoComplete synchronously, before anything async happens - for
        // an autoComplete=FALSE Stage that check is skipped in the very same call, so the state
        // Trigger(ManualStart) returns is deterministically still Active, not a race to be won. The
        // real proof this isn't wedged in some NEW bad way instead is a positive synchronization
        // point: an explicit manual Trigger(Complete) immediately afterward must still succeed
        // (Table 8.12's Manual Completion branch - requiredChildrenTerminal over an empty child set
        // - is vacuously satisfied too), proving the Stage is legitimately open for completion, it
        // just does not complete itself.
        [Fact]
        [ConformanceCitation("Table 8.12 / autoComplete=FALSE requires explicit completion, zero children (#180 follow-up)")]
        public async Task StageCompletion__Given_NotAutoCompleteStageWithZeroPlanItems__Then_DoesNotAutoCompleteButManualSucceeds()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_EmptyStageNotAutoCompleteRequiresManual.cmmn");

            var stageGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemStageEmpty", deployed.Scope);

            (await stageGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Enabled,
                "Table 5.51: StageEmpty declares no ManualActivationRule, so it waits Enabled for a Case worker");

            var stageSnapshot = await stageGrain.Trigger(PlanItemTransition.ManualStart);
            stageSnapshot.PlanItemState.Should().Be(PlanItemState.Active,
                "autoComplete=FALSE requires explicit completion (Table 8.12's Manual Completion " +
                "branch) - an empty Stage must not auto-complete just because it has zero children");

            var afterManualComplete = await stageGrain.Trigger(PlanItemTransition.Complete);
            afterManualComplete.PlanItemState.Should().Be(PlanItemState.Completed,
                "the Stage was never wedged - Table 8.12's Manual Completion branch is vacuously " +
                "satisfied by zero required children, so an explicit Trigger(Complete) succeeds " +
                "immediately");
        }

        // Table 8.12, autoComplete=TRUE column, follow-up: the column carries NO "no
        // DiscretionaryItems pending" term at all - that conjunct exists only in the
        // autoComplete=FALSE column's Branch 1 (StageBehavior.HandleChildTransitioned's
        // noDiscretionaryItemsPending check). StagePlanningOnly has zero fixed <planItem> children
        // - every child it could ever have is discretionary (5.4.9.2/8.7 - never auto-instantiated)
        // - plus a <planningTable> with one un-planned <discretionaryItem>. It completes exactly as
        // vacuously as a Stage with no PlanningTable at all: the #180 fix's gate checks only
        // AutoComplete and !PlanItems.Any(), deliberately NOT PlanningTable, because adding a
        // PlanningTable conjunct there would reintroduce a second, divergent definition of
        // "complete" alongside the shared predicate the fix exists to centralize. Pinned so a
        // future run-time-planning API (selecting DiscretionaryTaskA into the plan) has a
        // documented, deliberate baseline to design against, not an accidental gap.
        [Fact]
        [ConformanceCitation("Table 8.12 / autoComplete=TRUE has no DiscretionaryItems conjunct - all-discretionary Stage completes vacuously (#180 follow-up)")]
        public async Task StageCompletion__Given_AutoCompleteStageWithOnlyDiscretionaryItems__Then_CompletesVacuously()
        {
            var deployed = await _harness.DeployAndCreate("Lifecycle_StageAllDiscretionaryAutoCompletes.cmmn");

            var stageGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemStagePlanningOnly", deployed.Scope);

            (await stageGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Enabled,
                "Table 5.51: StagePlanningOnly declares no ManualActivationRule, so it waits Enabled for a Case worker");

            var stageSnapshot = await stageGrain.Trigger(PlanItemTransition.ManualStart);
            stageSnapshot.PlanItemState.Should().BeOneOf(PlanItemState.Active, PlanItemState.Completed);

            var completed = await ConformanceHarness.PollUntil(
                async () => (await stageGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed);

            completed.Should().BeTrue(
                "Table 8.12's autoComplete=TRUE column has no 'no DiscretionaryItems pending' term " +
                "(that conjunct exists only in the autoComplete=FALSE column's Branch 1) - a Stage " +
                "with zero fixed PlanItems and an un-planned PlanningTable completes exactly as " +
                "vacuously as one with no PlanningTable at all");
        }
    }
}
