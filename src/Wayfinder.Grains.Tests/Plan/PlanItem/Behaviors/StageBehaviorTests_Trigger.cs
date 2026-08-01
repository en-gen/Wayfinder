using System;
using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Moq;
using Xunit;

namespace Wayfinder.Grains.Tests.Plan.PlanItem.Behaviors
{
    // Table 8.12 - Stage instance termination criteria (8.6.1), manual-completion gate (D4).
    // ~~~~~
    // An externally-invoked Trigger(Complete) is "Manual Completion". For autoComplete=FALSE its
    // Table 8.12 branch requires ONLY that all required children are in
    // {Disabled, Completed, Terminated, Failed} - a still-Active NON-required child must not
    // block it. For autoComplete=TRUE the single column applies: no Active children AND required
    // children terminal. The gate evaluates child snapshots LIVE at the moment of the call.
    public partial class StageBehaviorTests
    {
        [Fact]
        public async Task Trigger__Given_NotAutoComplete__When_NonRequiredChildActive__Then_ManualCompleteSucceeds()
        {
            var (subject, mockMachine, _) = CreateTriggerSubject(
                autoComplete: false,
                childStates: new[]
                {
                    (required: true, state: PlanItemState.Completed),
                    (required: false, state: PlanItemState.Active)
                });

            await subject.Trigger(PlanItemTransition.Complete);

            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Once);
            mockMachine.Object.State.Should().Be(PlanItemState.Completed,
                "Table 8.12's manual-completion branch has no 'no Active children' conjunct - a non-required Active child must not block it");
        }

        [Fact]
        public async Task Trigger__Given_NotAutoComplete__When_RequiredChildActive__Then_ManualCompleteThrows()
        {
            var (subject, mockMachine, _) = CreateTriggerSubject(
                autoComplete: false,
                childStates: new[]
                {
                    (required: true, state: PlanItemState.Active),
                    (required: false, state: PlanItemState.Completed)
                });

            await Assert.ThrowsAsync<InvalidOperationException>(() => subject.Trigger(PlanItemTransition.Complete));

            mockMachine.Object.State.Should().Be(PlanItemState.Active,
                "a required child not yet in a terminal state must block manual completion");
        }

        [Fact]
        public async Task Trigger__Given_NotAutoComplete__When_RequiredChildEnabled__Then_ManualCompleteThrows()
        {
            var (subject, mockMachine, _) = CreateTriggerSubject(
                autoComplete: false,
                childStates: new[]
                {
                    (required: true, state: PlanItemState.Enabled)
                });

            await Assert.ThrowsAsync<InvalidOperationException>(() => subject.Trigger(PlanItemTransition.Complete));

            mockMachine.Object.State.Should().Be(PlanItemState.Active,
                "Enabled is not in {Disabled, Completed, Terminated, Failed} - a required Enabled child blocks manual completion");
        }

        [Fact]
        public async Task Trigger__Given_AutoComplete__When_NonRequiredChildActive__Then_ManualCompleteThrows()
        {
            var (subject, mockMachine, _) = CreateTriggerSubject(
                autoComplete: true,
                childStates: new[]
                {
                    (required: true, state: PlanItemState.Completed),
                    (required: false, state: PlanItemState.Active)
                });

            await Assert.ThrowsAsync<InvalidOperationException>(() => subject.Trigger(PlanItemTransition.Complete));

            mockMachine.Object.State.Should().Be(PlanItemState.Active,
                "autoComplete=true has a single Table 8.12 column - no Active children at all - and any Complete must satisfy it");
        }

        [Fact]
        public async Task Trigger__Given_AutoComplete__When_ChildrenTerminalOrEnabled__Then_ManualCompleteSucceeds()
        {
            var (subject, mockMachine, _) = CreateTriggerSubject(
                autoComplete: true,
                childStates: new[]
                {
                    (required: true, state: PlanItemState.Completed),
                    (required: false, state: PlanItemState.Enabled)
                });

            await subject.Trigger(PlanItemTransition.Complete);

            mockMachine.Object.State.Should().Be(PlanItemState.Completed,
                "an Enabled (not Active) non-required child satisfies 'no Active children', and all required children are terminal");
        }

        [Fact]
        public async Task Trigger__Given_NoChildren__Then_ManualCompleteSucceeds()
        {
            var (subject, mockMachine, _) = CreateTriggerSubject(
                autoComplete: false,
                childStates: Array.Empty<(bool, PlanItemState)>());

            await subject.Trigger(PlanItemTransition.Complete);

            mockMachine.Object.State.Should().Be(PlanItemState.Completed,
                "Table 8.12's criteria are vacuously satisfied for a stage with no child instances");
        }

        [Fact]
        public async Task Trigger__Given_NotCompletable__When_NonCompleteTransition__Then_NotGated()
        {
            var (subject, mockMachine, _) = CreateTriggerSubject(
                autoComplete: false,
                childStates: new[]
                {
                    (required: true, state: PlanItemState.Active)
                });

            // Suspend is not gated by Table 8.12 - it must pass straight through even while a
            // required child is Active (i.e. while Complete WOULD be rejected)
            await subject.Trigger(PlanItemTransition.Suspend);

            mockMachine.Object.State.Should().Be(PlanItemState.Suspended);
        }

        [Fact]
        public async Task Trigger__Given_CompleteNotFireable__Then_FallsThroughToSilentNoOp()
        {
            // From Enabled the machine cannot fire Complete at all - the gate must not turn that
            // pre-existing silent unhandled-trigger no-op into a throw. (Trigger's CanFire check
            // also short-circuits before it fetches any child snapshot; that ordering is not
            // asserted here, only the observable outcome.)
            var (subject, mockMachine, _) = CreateTriggerSubject(
                autoComplete: false,
                childStates: new[]
                {
                    (required: true, state: PlanItemState.Active)
                },
                initialState: PlanItemState.Enabled);

            await subject.Trigger(PlanItemTransition.Complete);

            mockMachine.Object.State.Should().Be(PlanItemState.Enabled,
                "an unfireable Complete stays the existing silent no-op regardless of Table 8.12");
        }

        // A thin projection of the canonical subject builder in
        // StageBehaviorTests_RepetitionCompletionGate.cs (same partial class): these scenarios
        // predate the #198 gate and care only about Table 8.12's own criteria, so every child here
        // is simply one that never declared a repetition.
        private (StageBehavior subject, MockPlanItemStateMachine machine, Mock<IBehaviorHost> host) CreateTriggerSubject(
            bool autoComplete,
            (bool required, PlanItemState state)[] childStates,
            PlanItemState initialState = PlanItemState.Active)
        {
            var (subject, machine, host, _, _) = CreateStageSubject(
                autoComplete,
                childStates
                    .Select(x => (x.required, x.state, repeated: false, settled: (SettlementKind?)null))
                    .ToArray(),
                initialState);

            return (subject, machine, host);
        }
    }
}
