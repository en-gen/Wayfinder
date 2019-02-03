using System.Reflection;
using System.Threading.Tasks;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Tests.Helpers;
using Moq;
using Xunit;

namespace Flow.Grains.Tests.Plan.PlanItem.Behaviors
{
    public partial class TaskBehaviorTests
    {
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
