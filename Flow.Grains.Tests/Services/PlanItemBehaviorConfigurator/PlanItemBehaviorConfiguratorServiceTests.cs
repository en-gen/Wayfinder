using System;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Flow.Grains.Services.PlanItemBehaviorConfigurator;
using Flow.Grains.Services.PlanItemStateMachineConfigurator;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Flow.Grains.Tests.Services.PlanItemBehaviorConfigurator
{
    public class PlanItemBehaviorConfiguratorServiceTests
    {
        [Theory, AutoData]
        public async Task Given_Context__When_DefinitionMilestone__Then_CreateMilestoneContext(Guid caseDefinitionId)
        {
            var planItem = new PlanItem();
            var planItemStore = new PlanItemStore();

            planItemStore.Apply(new Defined
            {
                CaseDefinitionId = caseDefinitionId,
                Definition = planItem,
                PlanItemDefinition = new Milestone()
            });
            
            var host = new Mock<IBehaviorHost>();
            host.Setup(x => x.State)
                .Returns(planItemStore);
            host.Setup(x => x.Definition)
                .Returns(planItem);

            var mockMachine = CreateMockStateMachine(planItemStore);

            var mockStateMachineConfigurator = new Mock<IPlanItemStateMachineConfigurator>();
            mockStateMachineConfigurator
                .Setup(x => x.Configure(planItemStore))
                .Returns(mockMachine.Object);

            var subject = new PlanItemBehaviorConfiguratorService(mockStateMachineConfigurator.Object);

            var result = await subject.Configure(host.Object, new Milestone());

            result.Should().BeOfType<MilestoneBehavior>();
        }

        private Mock<IPlanItemStateMachine> CreateMockStateMachine(PlanItemStore store)
        {
            if (store.PlanItemDefinition == null) throw new ArgumentNullException(nameof(store.PlanItemDefinition), "You forgot to set the store's PlanItemDefinition (again)");

            var machine = new PlanItemStateMachine(store, Mock.Of<ILogger<PlanItemStateMachine>>());

            var mock = new Mock<IPlanItemStateMachine>();
            mock
                .Setup(x => x.Configure(It.IsAny<PlanItemState>()))
                .Returns<PlanItemState>(x => machine.Configure(x));
            mock
                .Setup(x => x.CanFire(It.IsAny<PlanItemTransition>()))
                .Returns<PlanItemTransition>(x => machine.CanFire(x));

            mock
                .Setup(x => x.FireAsync(It.IsAny<PlanItemTransition>()))
                .Returns<PlanItemTransition>(x => machine.FireAsync(x));

            return mock;
        }
    }
}
