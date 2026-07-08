using System;
using System.Reflection;
using System.Threading.Tasks;
using Flow.Grains.Expressions;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Moq;
using Orleans;
using Xunit;

namespace Flow.Grains.Tests.Plan.PlanItem.Behaviors
{
    public partial class TaskBehaviorTests
    {
        // Pinning test for the Task.Factory.StartNew(async () => ...) anti-pattern that used to wrap
        // this branch: StartNew with an async lambda returns Task<Task>, so the inner task (and any
        // exception it throws) was never awaited/observed by the Task.WhenAll in
        // HandleEnterAvailableFromCreate. Now that the branch is a directly-awaited call, an exception
        // thrown while evaluating the ManualActivationRule must propagate out of
        // HandleEnterAvailableFromCreate instead of vanishing.
        [Fact]
        public async Task HandleEnterAvailableFromCreate__When_NoEntryCriteriaAndManualActivationRuleThrows__Then_ExceptionSurfaces()
        {
            var caseInstanceId = Guid.NewGuid();

            var task = new HumanTask();

            var manualActivationRule = Rules.IsManuallyActivated;

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = task.Id,
                ItemControl = new PlanItemControl
                {
                    ManualActivationRule = manualActivationRule
                }
            };

            var testStore = new TestPlanItemStore(piDef: task, def: pi, initialState: PlanItemState.Uninitialized);

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain
                .Setup(x => x.ExecuteAsBool(manualActivationRule.ContextRef, manualActivationRule.Condition))
                .ThrowsAsync(new InvalidOperationException("boom"));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.State)
                .Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic) x));
            mockHost.Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            Func<Task> act = () => (Task) typeof(TaskBehavior<HumanTask>)
                .GetMethod("HandleEnterAvailableFromCreate", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[0]);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        }

        [Fact]
        public async Task HandleEnterActive__When_NotIsBlocking__Then_TransitionToComplete()
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var task = new HumanTask
            {
                IsBlocking = false
            };

            var testStore = new TestPlanItemStore(piDef: task);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
                .GetMethod("HandleEnterActive", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[0]);

            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Once);
        }

        [Fact]
        public async Task HandleEnterActive__When_IsBlocking__Then_TransitionToComplete()
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var task = new HumanTask();

            var testStore = new TestPlanItemStore(piDef: task);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
                .GetMethod("HandleEnterActive", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[0]);

            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>()), Times.Never);
        }
    }
}
