using AutoFixture.Xunit2;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Plan.PlanItem.StateMachine;
using Wayfinder.Grains.Services.PlanItemStateMachineConfigurator;
using Wayfinder.Grains.Tests.Infrastructure.Extensions;
using Wayfinder.Grains.Tests.Infrastructure.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Wayfinder.Grains.Tests.Services.PlanItemStateMachineConfigurator
{
    public class PlanItemStateMachineConfiguratorServiceTests
    {
        private static readonly string PlanItemStateMachineTypeName = TypeNameHelper.GetTypeDisplayName(typeof(PlanItemStateMachine));

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
                .Setup(x => x.CreateLogger(PlanItemStateMachineTypeName))
                .Returns(Mock.Of<ILogger<PlanItemStateMachine>>());

            return new PlanItemStateMachineConfiguratorService(logFactory.Object);
        }
    }
}
