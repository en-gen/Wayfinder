using AutoFixture.Xunit2;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Flow.Grains.Services.PlanItemStateMachineConfigurator;
using Flow.Grains.Tests.Infrastructure.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions.Internal;
using Moq;
using Xunit;

namespace Flow.Grains.Tests.Services.PlanItemStateMachineConfigurator
{
    public class PlanItemStateMachineConfiguratorServiceTests
    {
        [Theory, AutoData]
        public void Configure__Given_MilestoneStore__Then_MilestoneStateMachine(string caseDefinitionId)
        {
            var store = new PlanItemStore();
            store.Apply(new Defined
            {
               CaseDefinitionId = caseDefinitionId,
               PlanItemDefinition = new Milestone()
            });

            var subject = CreateSubject();
            
            var stateMachine = subject.Configure(store);

            var info = stateMachine.GetInfo();

            info.Should().BeMilestoneOrEventListenerMachine();
        }

        private PlanItemStateMachineConfiguratorService CreateSubject()
        {
            var logFactory = new Mock<ILoggerFactory>();
            logFactory
                .Setup(x => x.CreateLogger(TypeNameHelper.GetTypeDisplayName(typeof(PlanItemStateMachine))))
                .Returns(Mock.Of<ILogger<PlanItemStateMachine>>());
            
            return new PlanItemStateMachineConfiguratorService(logFactory.Object);
        }
    }
}
