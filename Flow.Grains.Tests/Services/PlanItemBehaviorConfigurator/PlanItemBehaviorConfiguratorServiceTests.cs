using System;
using AutoFixture.Xunit2;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Flow.Grains.Services.PlanItemBehaviorConfigurator;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Flow.Grains.Tests.Services.PlanItemBehaviorConfigurator
{
    public class PlanItemBehaviorConfiguratorServiceTests
    {
        [Theory, AutoData]
        public void Given_Context__When_DefinitionMilestone__Then_CreateMilestoneContext(Guid caseDefinitionId)
        {
            var planItem = new PlanItem();
            var planItemStore = new PlanItemStore();

            planItemStore.Apply(new Defined
            {
                CaseDefinitionId = caseDefinitionId,
                Definition = planItem,
                PlanItemDefinition = new Milestone()
            });
            
            var stateMachine = new PlanItemStateMachine(planItemStore, Mock.Of<ILogger<PlanItemStateMachine>>());

            var host = new Mock<IBehaviorHost>();
            host
                .SetupGet(x => x.StateMachine)
                .Returns(stateMachine);

            var subject = new PlanItemBehaviorConfiguratorService();

            var result = subject.Configure(host.Object, new Milestone());

            result.Should().BeOfType<MilestoneBehavior>();
        }
    }
}
