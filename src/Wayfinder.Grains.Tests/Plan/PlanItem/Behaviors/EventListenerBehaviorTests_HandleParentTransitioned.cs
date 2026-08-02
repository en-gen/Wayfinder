using System.Reflection;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Moq;
using Orleans.Streams;
using Xunit;

namespace Wayfinder.Grains.Tests.Plan.PlanItem.Behaviors
{
    public partial class EventListenerBehaviorTests
    {
        [Fact]
        public async Task HandleParentTransitioned__When_ParentInstanceIdEventSource__Then_Disregard()
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var eventListener = new EventListener();

            var testStore = new TestPlanItemStore(piDef: eventListener);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new EventListenerBehavior<EventListener>(mockHost.Object, eventListener, mockMachine.Object);

            await (Task)typeof(EventListenerBehavior<EventListener>)
                .GetMethod("HandleParentTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        "CPM",
                        "not_a_match",
                        "ParentStage",
                        PlanItemTransition.ParentSuspend,
                        PlanItemState.Active,
                        PlanItemState.Suspended),
                    (StreamSequenceToken)null
                });

            mockMachine.Object.State.Should().Be(PlanItemState.Available);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<object>()), Times.Never);
            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>()), Times.Never);
        }

        [Theory]
        [InlineData(PlanItemTransition.Suspend)]
        [InlineData(PlanItemTransition.ParentSuspend)]
        public async Task HandleParentTransitioned__When_ParentSuspended__Then_RaiseEventAndTransition
            (PlanItemTransition parentTransition)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var eventListener = new EventListener();

            var testStore = new TestPlanItemStore(piDef: eventListener);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.State)
                .Returns(testStore);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new EventListenerBehavior<EventListener>(mockHost.Object, eventListener, mockMachine.Object);

            await (Task)typeof(EventListenerBehavior<EventListener>)
                .GetMethod("HandleParentTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        "CPM",
                        parentInstanceId,
                        "ParentStage",
                        parentTransition,
                        PlanItemState.Active,
                        PlanItemState.Suspended),
                    (StreamSequenceToken)null
                });

            mockMachine.Object.State.Should().Be(PlanItemState.Suspended);
            mockMachine.Object.ParentSuspendState.Should().BeNull();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ParentSuspended>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Suspend), Times.Once);
        }

        [Theory]
        [InlineData(PlanItemTransition.Resume)]
        [InlineData(PlanItemTransition.ParentResume)]
        public async Task HandleParentTransitioned__When_ParentResumed__Then_ResumeToPreviousState
            (PlanItemTransition parentTransition)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var eventListener = new EventListener();

            var testStore = new TestPlanItemStore(piDef: eventListener);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.State)
                .Returns(testStore);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new EventListenerBehavior<EventListener>(mockHost.Object, eventListener, mockMachine.Object);

            // we must ParentSuspend before we can ParentResume
            await (Task)typeof(EventListenerBehavior<EventListener>)
                .GetMethod("HandleParentTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        "CPM",
                        parentInstanceId,
                        "ParentStage",
                        PlanItemTransition.ParentSuspend,
                        PlanItemState.Active,
                        PlanItemState.Suspended),
                    (StreamSequenceToken)null
                });

            mockMachine.Object.State.Should().Be(PlanItemState.Suspended);
            mockMachine.Object.ParentSuspendState.Should().BeNull();

            await (Task)typeof(EventListenerBehavior<EventListener>)
                .GetMethod("HandleParentTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        "CPM",
                        parentInstanceId,
                        "ParentStage",
                        parentTransition,
                        PlanItemState.Suspended,
                        PlanItemState.Active),
                    (StreamSequenceToken)null
                });

            mockMachine.Object.ParentSuspendState.Should().BeNull();
            mockMachine.Object.State.Should().Be(PlanItemState.Available);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ParentResumed>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Resume), Times.Once);
        }

        [Theory]
        [InlineData(PlanItemTransition.Exit)]
        [InlineData(PlanItemTransition.Terminate)]
        public async Task HandleParentTransitioned__When_ParentTerminated__Then_Exit
            (PlanItemTransition parentTransition)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var eventListener = new EventListener();

            var testStore = new TestPlanItemStore(piDef: eventListener, def: pi);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.State)
                .Returns(testStore);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new EventListenerBehavior<EventListener>(mockHost.Object, eventListener, mockMachine.Object);

            await (Task)typeof(EventListenerBehavior<EventListener>)
                .GetMethod("HandleParentTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        "CPM",
                        parentInstanceId,
                        "ParentStage",
                        parentTransition,
                        PlanItemState.Active,
                        PlanItemState.Suspended),
                    (StreamSequenceToken)null
                });

            mockMachine.Object.ParentSuspendState.Should().BeNull();
            mockMachine.Object.State.Should().Be(PlanItemState.Terminated);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ParentTerminated>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.ParentTerminate), Times.Once);
        }

        // #216 phase 1 (characterization) - THE asymmetry guard, EventListener half. See
        // MilestoneBehaviorTests' twin of this test for the full Table 8.9 rationale: Milestone and
        // EventListener share a column in that table's `complete` rows and both legitimately REMAIN
        // Available/Suspended under a Completed parent, where Stage and Task instances in those
        // states are `<impossible>` and must be cascaded out via `exit` (#179).
        //
        // `ParentCompleted` had zero coverage repo-wide before this, so giving EventListenerBehavior
        // a Complete arm would have left the entire suite green while violating Table 8.9.
        [Theory]
        [InlineData(PlanItemState.Available)]
        [InlineData(PlanItemState.Suspended)]
        public async Task HandleParentTransitioned__When_ParentCompleted__Then_NoEventAndNoTransition
            (PlanItemState hostState)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var eventListener = new EventListener();

            var testStore = new TestPlanItemStore(piDef: eventListener, def: pi, initialState: hostState);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.State)
                .Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new EventListenerBehavior<EventListener>(mockHost.Object, eventListener, mockMachine.Object);

            await (Task)typeof(EventListenerBehavior<EventListener>)
                .GetMethod("HandleParentTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        "CPM",
                        parentInstanceId,
                        "ParentStage",
                        PlanItemTransition.Complete,
                        PlanItemState.Active,
                        PlanItemState.Completed),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ParentCompleted>()), Times.Never,
                "Table 8.9 permits an EventListener child to survive a completing parent - giving EventListenerBehavior a Complete arm would be a conformance violation");
            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>()), Times.Never);

            mockMachine.Object.State.Should().Be(hostState,
                "Table 8.9's `complete` row keeps an EventListener child Available -> Available and Suspended -> Suspended");
            testStore.PlanItemState.Should().Be(hostState);
        }

        // #216 phase 1 (characterization) - Table 8.9's parent-transition column for an
        // EventListener child, made executable rather than a comment. See MilestoneBehaviorTests'
        // twin for the rationale; the vocabulary is identical between the two types
        // (Suspend/Resume/ParentTerminate, NOT Stage/Task's ParentSuspend/ParentResume/Exit) because
        // both are configured by ConfigureForMilestoneOrEventListener.
        //
        // Reactivate is included because it is the CasePlanModel's own way out of Suspended (Table
        // 8.6 has no Resume edge for the Case at all - #63 D8); no existing test covered that arm
        // on this type.
        [Theory]
        [InlineData(PlanItemTransition.Suspend, PlanItemState.Available, PlanItemTransition.Suspend, PlanItemState.Suspended)]
        [InlineData(PlanItemTransition.ParentSuspend, PlanItemState.Available, PlanItemTransition.Suspend, PlanItemState.Suspended)]
        [InlineData(PlanItemTransition.Resume, PlanItemState.Suspended, PlanItemTransition.Resume, PlanItemState.Available)]
        [InlineData(PlanItemTransition.ParentResume, PlanItemState.Suspended, PlanItemTransition.Resume, PlanItemState.Available)]
        [InlineData(PlanItemTransition.Reactivate, PlanItemState.Suspended, PlanItemTransition.Resume, PlanItemState.Available)]
        [InlineData(PlanItemTransition.Exit, PlanItemState.Available, PlanItemTransition.ParentTerminate, PlanItemState.Terminated)]
        [InlineData(PlanItemTransition.Terminate, PlanItemState.Available, PlanItemTransition.ParentTerminate, PlanItemState.Terminated)]
        public async Task HandleParentTransitioned__When_ParentTransitioned__Then_FiresThisTypesTable89Trigger(
            PlanItemTransition parentTransition,
            PlanItemState hostState,
            PlanItemTransition expectedOwnTrigger,
            PlanItemState expectedDestination)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var eventListener = new EventListener();

            var testStore = new TestPlanItemStore(piDef: eventListener, def: pi, initialState: hostState);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.State)
                .Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new EventListenerBehavior<EventListener>(mockHost.Object, eventListener, mockMachine.Object);

            await (Task)typeof(EventListenerBehavior<EventListener>)
                .GetMethod("HandleParentTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        "CPM",
                        parentInstanceId,
                        "ParentStage",
                        parentTransition,
                        PlanItemState.Active,
                        PlanItemState.Suspended),
                    (StreamSequenceToken)null
                });

            mockMachine.Verify(x => x.FireAsync(expectedOwnTrigger), Times.Once);

            // Asserted alongside the FireAsync verification, not instead of it: a wrong trigger
            // that happens to be unpermitted from this state would otherwise no-op silently
            // (HandleParentTransitioned gates every fire on CanFire), leaving only the destination
            // state to catch it.
            mockMachine.Object.State.Should().Be(expectedDestination);
            testStore.PlanItemState.Should().Be(expectedDestination);
        }
    }
}
