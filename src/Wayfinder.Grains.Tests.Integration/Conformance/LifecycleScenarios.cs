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
    }
}
