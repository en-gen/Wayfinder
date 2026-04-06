using System;
using System.Reflection;
using System.Threading.Tasks;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.PlanningTable;
using Flow.Grains.Tests.Utils.Helpers;
using Moq;
using Orleans;
using Xunit;

namespace Flow.Grains.Tests.Plan.PlanItem.Behaviors
{
    public class HumanTaskBehaviorTests
    {
        [Fact]
        public async Task Define__Given_PlanItemDefinition__When_NoPlanningTable__Then_DoNothing()
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

            var subject = new HumanTaskBehavior(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
                .GetMethod("Define", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[0]);

            mockHost.Verify(x => x.GrainFactory, Times.Never);
        }

        [Fact]
        public async Task Define__Given_PlanItemDefinition__When_PlanningTableDefined__Then_DoNothing()
        {
            var caseInstanceId = Guid.NewGuid();
            var parentInstanceId = ShortGuid.NewGuid();
            var instanceId = ShortGuid.NewGuid();

            var planningTable = new Interfaces.Model.PlanningTable();

            var task = new HumanTask
            {
                IsBlocking = false,
                PlanningTable = planningTable
            };

            var testStore = new TestPlanItemStore(piDef: task);

            var mockPlanningTable = new Mock<IPlanningTableGrain>();
            mockPlanningTable.Setup(x => x.Defined())
                .Returns(Task.FromResult(true));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanningTableGrain>(caseInstanceId, instanceId, null))
                .Returns(mockPlanningTable.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.InstanceId)
                .Returns(instanceId);
            mockHost.Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new HumanTaskBehavior(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
                .GetMethod("Define", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[0]);

            mockPlanningTable.Verify(x => x.Define(testStore.CaseDefinitionId, planningTable), Times.Never);
        }

        [Fact]
        public async Task Define__Given_PlanItemDefinition__When_PlanningTableNotDefined__Then_Define()
        {
            var caseInstanceId = Guid.NewGuid();
            var parentInstanceId = ShortGuid.NewGuid();
            var instanceId = ShortGuid.NewGuid();

            var planningTable = new Interfaces.Model.PlanningTable();

            var task = new HumanTask
            {
                IsBlocking = false,
                PlanningTable = planningTable
            };

            var testStore = new TestPlanItemStore(piDef: task);

            var mockPlanningTable = new Mock<IPlanningTableGrain>();
            mockPlanningTable.Setup(x => x.Defined())
                .Returns(Task.FromResult(false));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanningTableGrain>(caseInstanceId, instanceId, null))
                .Returns(mockPlanningTable.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.InstanceId)
                .Returns(instanceId);
            mockHost.Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);
            mockHost.Setup(x => x.State)
                .Returns(testStore);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new HumanTaskBehavior(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
                .GetMethod("Define", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[0]);

            mockPlanningTable.Verify(x => x.Define(testStore.CaseDefinitionId, planningTable), Times.Once);
        }
    }
}
