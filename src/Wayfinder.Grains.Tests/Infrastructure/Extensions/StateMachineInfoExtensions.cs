using System.Linq;
using Flow.Grains.Interfaces.Model;
using FluentAssertions;
using FluentAssertions.Primitives;
using Stateless.Reflection;

namespace Flow.Grains.Tests.Infrastructure.Extensions
{
    public static class StateMachineInfoExtensions
    {
        public static AndConstraint<ObjectAssertions> BeMilestoneOrEventListenerMachine(this ObjectAssertions should)
        {
            should.BeAssignableTo<StateMachineInfo>();

            var info = (StateMachineInfo)should.Subject;

            info.StateType.Should().Be<PlanItemState>();
            info.TriggerType.Should().Be<PlanItemTransition>();

            info.States.Should().NotBeEmpty()
                .And.HaveCount(5)
                .And.Equal(new[]
                {
                    PlanItemState.Uninitialized,
                    PlanItemState.Available,
                    PlanItemState.Suspended,
                    PlanItemState.Terminated,
                    PlanItemState.Completed
                }, (i, s) => (PlanItemState)i.UnderlyingState == s);

            var uninitializedStateInfo = info.States.Single(x => (PlanItemState)x.UnderlyingState == PlanItemState.Uninitialized);
            var availableStateInfo = info.States.Single(x => (PlanItemState)x.UnderlyingState == PlanItemState.Available);
            var suspendedStateInfo = info.States.Single(x => (PlanItemState)x.UnderlyingState == PlanItemState.Suspended);
            var terminatedStateInfo = info.States.Single(x => (PlanItemState)x.UnderlyingState == PlanItemState.Terminated);
            var completedStateInfo = info.States.Single(x => (PlanItemState)x.UnderlyingState == PlanItemState.Completed);

            uninitializedStateInfo.FixedTransitions.Should().NotBeEmpty()
                .And.HaveCount(1)
                .And.Equal(new[]
                    {
                        new {Trigger = PlanItemTransition.Create, Destination = PlanItemState.Available}
                    },
                    (i, x) => (PlanItemTransition)i.Trigger.UnderlyingTrigger == x.Trigger &&
                              (PlanItemState)i.DestinationState.UnderlyingState == x.Destination);

            availableStateInfo.FixedTransitions.Should().NotBeEmpty()
                .And.HaveCount(4)
                .And.Equal(new[]
                    {
                        new {Trigger = PlanItemTransition.Suspend, Destination = PlanItemState.Suspended},
                        new {Trigger = PlanItemTransition.Terminate, Destination = PlanItemState.Terminated},
                        new {Trigger = PlanItemTransition.Occur, Destination = PlanItemState.Completed},
                        new {Trigger = PlanItemTransition.ParentTerminate, Destination = PlanItemState.Terminated}
                    },
                    (i, x) => (PlanItemTransition)i.Trigger.UnderlyingTrigger == x.Trigger &&
                              (PlanItemState)i.DestinationState.UnderlyingState == x.Destination);

            suspendedStateInfo.FixedTransitions.Should().NotBeEmpty()
                .And.HaveCount(2)
                .And.Equal(new[]
                    {
                        new {Trigger = PlanItemTransition.Resume, Destination = PlanItemState.Available},
                        new {Trigger = PlanItemTransition.ParentTerminate, Destination = PlanItemState.Terminated}
                    },
                    (i, x) => (PlanItemTransition)i.Trigger.UnderlyingTrigger == x.Trigger &&
                              (PlanItemState)i.DestinationState.UnderlyingState == x.Destination);

            terminatedStateInfo.FixedTransitions.Should().BeEmpty();
            completedStateInfo.FixedTransitions.Should().BeEmpty();

            return info.Should().NotBeNull();
        }

        public static AndConstraint<ObjectAssertions> BeStageOrTaskMachine(this ObjectAssertions should)
        {
            should.BeAssignableTo<StateMachineInfo>();

            var info = (StateMachineInfo)should.Subject;

            info.StateType.Should().Be<PlanItemState>();
            info.TriggerType.Should().Be<PlanItemTransition>();

            info.States.Should().NotBeEmpty()
                .And.HaveCount(9)
                .And.Equal(new[]
                {
                    PlanItemState.Uninitialized,
                    PlanItemState.Available,
                    PlanItemState.Enabled,
                    PlanItemState.Disabled,
                    PlanItemState.Active,
                    PlanItemState.Suspended,
                    PlanItemState.Failed,
                    PlanItemState.Terminated,
                    PlanItemState.Completed
                }, (i, s) => (PlanItemState)i.UnderlyingState == s);


            var uninitializedStateInfo = info.States.Single(x => (PlanItemState)x.UnderlyingState == PlanItemState.Uninitialized);
            var availableStateInfo = info.States.Single(x => (PlanItemState)x.UnderlyingState == PlanItemState.Available);
            var enabledStateInfo = info.States.Single(x => (PlanItemState)x.UnderlyingState == PlanItemState.Enabled);
            var disabledStateInfo = info.States.Single(x => (PlanItemState)x.UnderlyingState == PlanItemState.Disabled);
            var activeStateInfo = info.States.Single(x => (PlanItemState)x.UnderlyingState == PlanItemState.Active);
            var suspendedStateInfo = info.States.Single(x => (PlanItemState)x.UnderlyingState == PlanItemState.Suspended);
            var failedStateInfo = info.States.Single(x => (PlanItemState)x.UnderlyingState == PlanItemState.Failed);
            var terminatedStateInfo = info.States.Single(x => (PlanItemState)x.UnderlyingState == PlanItemState.Terminated);
            var completedStateInfo = info.States.Single(x => (PlanItemState)x.UnderlyingState == PlanItemState.Completed);

            uninitializedStateInfo.FixedTransitions.Should().NotBeEmpty()
                .And.HaveCount(1)
                .And.Equal(new[]
                    {
                        new {Trigger = PlanItemTransition.Create, Destination = PlanItemState.Available}
                    },
                    (i, x) => (PlanItemTransition)i.Trigger.UnderlyingTrigger == x.Trigger &&
                              (PlanItemState)i.DestinationState.UnderlyingState == x.Destination);


            availableStateInfo.FixedTransitions.Should().NotBeEmpty()
                .And.HaveCount(4)
                .And.Equal(new[]
                    {
                        new {Trigger = PlanItemTransition.Enable, Destination = PlanItemState.Enabled},
                        new {Trigger = PlanItemTransition.Start, Destination = PlanItemState.Active},
                        new {Trigger = PlanItemTransition.Exit, Destination = PlanItemState.Terminated},
                        new {Trigger = PlanItemTransition.ParentSuspend, Destination = PlanItemState.Suspended}
                    },
                    (i, x) => (PlanItemTransition)i.Trigger.UnderlyingTrigger == x.Trigger &&
                              (PlanItemState)i.DestinationState.UnderlyingState == x.Destination);

            enabledStateInfo.FixedTransitions.Should().NotBeEmpty()
                .And.HaveCount(4)
                .And.Equal(new[]
                {
                    new {Trigger = PlanItemTransition.Disable, Destination = PlanItemState.Disabled},
                    new {Trigger = PlanItemTransition.ManualStart, Destination = PlanItemState.Active},
                    new {Trigger = PlanItemTransition.Exit, Destination = PlanItemState.Terminated},
                    new {Trigger = PlanItemTransition.ParentSuspend, Destination = PlanItemState.Suspended}
                },
                    (i, x) => (PlanItemTransition)i.Trigger.UnderlyingTrigger == x.Trigger &&
                              (PlanItemState)i.DestinationState.UnderlyingState == x.Destination);

            disabledStateInfo.FixedTransitions.Should().NotBeEmpty()
                .And.HaveCount(3)
                .And.Equal(new[]
                    {
                        new {Trigger = PlanItemTransition.Reenable, Destination = PlanItemState.Enabled},
                        new {Trigger = PlanItemTransition.Exit, Destination = PlanItemState.Terminated},
                        new {Trigger = PlanItemTransition.ParentSuspend, Destination = PlanItemState.Suspended},
                    },
                    (i, x) => (PlanItemTransition)i.Trigger.UnderlyingTrigger == x.Trigger &&
                              (PlanItemState)i.DestinationState.UnderlyingState == x.Destination);

            activeStateInfo.FixedTransitions.Should().NotBeEmpty()
                .And.HaveCount(6)
                .And.Equal(new[]
                    {
                        new {Trigger = PlanItemTransition.Suspend, Destination = PlanItemState.Suspended},
                        new {Trigger = PlanItemTransition.Fault, Destination = PlanItemState.Failed},
                        new {Trigger = PlanItemTransition.Complete, Destination = PlanItemState.Completed},
                        new {Trigger = PlanItemTransition.Terminate, Destination = PlanItemState.Terminated},
                        new {Trigger = PlanItemTransition.Exit, Destination = PlanItemState.Terminated},
                        new {Trigger = PlanItemTransition.ParentSuspend, Destination = PlanItemState.Suspended}
                    },
                    (i, x) => (PlanItemTransition)i.Trigger.UnderlyingTrigger == x.Trigger &&
                              (PlanItemState)i.DestinationState.UnderlyingState == x.Destination);

            suspendedStateInfo.FixedTransitions.Should().NotBeEmpty()
                .And.HaveCount(2)
                .And.Equal(new[]
                    {
                        new {Trigger = PlanItemTransition.Resume, Destination = PlanItemState.Active},
                        new {Trigger = PlanItemTransition.Exit, Destination = PlanItemState.Terminated}
                    },
                    (i, x) => (PlanItemTransition)i.Trigger.UnderlyingTrigger == x.Trigger &&
                              (PlanItemState)i.DestinationState.UnderlyingState == x.Destination);

            failedStateInfo.FixedTransitions.Should().NotBeEmpty()
                .And.HaveCount(2)
                .And.Equal(new[]
                    {
                        new {Trigger = PlanItemTransition.Reactivate, Destination = PlanItemState.Active},
                        new {Trigger = PlanItemTransition.Exit, Destination = PlanItemState.Terminated}
                    },
                    (i, x) => (PlanItemTransition)i.Trigger.UnderlyingTrigger == x.Trigger &&
                              (PlanItemState)i.DestinationState.UnderlyingState == x.Destination);

            terminatedStateInfo.FixedTransitions.Should().BeEmpty();
            completedStateInfo.FixedTransitions.Should().BeEmpty();

            return info.Should().NotBeNull();
        }
    }
}
