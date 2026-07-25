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
    public partial class StageBehaviorTests
    {
        [Fact]
        public async Task HandleParentTransitioned__When_ParentInstanceIdEventSource__Then_Disregard()
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
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

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
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
            mockMachine.Object.ParentSuspendState.Should().Be(PlanItemState.Available);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ParentSuspended>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.ParentSuspend), Times.Once);
        }

        [Theory]
        [InlineData(PlanItemTransition.Resume)]
        [InlineData(PlanItemTransition.ParentResume)]
        public async Task HandleParentTransitioned__When_ParentResumed__Then_ResumeToPreviousState
            (PlanItemTransition parentTransition)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            // we must ParentSuspend before we can ParentResume
            await (Task)typeof(StageBehavior)
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
            mockMachine.Object.ParentSuspendState.Should().Be(PlanItemState.Available);

            await (Task)typeof(StageBehavior)
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
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.ParentResume), Times.Once);
        }

        [Theory]
        [InlineData(PlanItemTransition.Exit)]
        [InlineData(PlanItemTransition.Terminate)]
        public async Task HandleParentTransitioned__When_ParentTerminated__Then_Exit
            (PlanItemTransition parentTransition)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage, def: pi);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
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
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Exit), Times.Once);
        }
    }
}
