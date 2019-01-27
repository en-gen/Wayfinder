using System;
using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Flow.Grains.Tests.Infrastructure.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Flow.Grains.Tests.Plan.PlanItem.StateMachine
{
    public class PlanItemStateMachineTests
    {
        [Theory]
        [InlineData(typeof(Milestone))]
        [InlineData(typeof(TimerEventListener))]
        [InlineData(typeof(UserEventListener))]
        public void Ctor__When_MilestoneOrEventListener__Then_ConfigureForMilestoneOrEventListener(Type piDefType)
        {
            var piDef = (PlanItemDefinition)Activator.CreateInstance(piDefType);

            var store = CreateStore(piDef: piDef);

            var subject = new PlanItemStateMachine(store, Mock.Of<ILogger<PlanItemStateMachine>>());

            subject.GetInfo().Should().BeMilestoneOrEventListenerMachine();
        }

        [Theory]
        [InlineData(typeof(Stage))]
        [InlineData(typeof(HumanTask))]
        public void Ctor__When_StageOrTask__Then_ConfigureForStageOrTask(Type piDefType)
        {
            var piDef = (PlanItemDefinition) Activator.CreateInstance(piDefType);

            var store = CreateStore(piDef: piDef);

            var subject = new PlanItemStateMachine(store, Mock.Of<ILogger<PlanItemStateMachine>>());

            subject.GetInfo().Should().BeStageOrTaskMachine();
        }

        [Fact]
        public async Task Ctor__When_ParentSuspendState__Then_SetParentSuspendState()
        {
            var preSuspendState = PlanItemState.Active;

            var store = CreateStore(piDef: new HumanTask(), initialState: preSuspendState);

            var subject = new PlanItemStateMachine(store, Mock.Of<ILogger<PlanItemStateMachine>>());

            await subject.FireAsync(PlanItemTransition.ParentSuspend);

            subject.ParentSuspendState.Should().Be(preSuspendState);

            await subject.FireAsync(PlanItemTransition.ParentResume);

            subject.State.Should().Be(preSuspendState);
            subject.ParentSuspendState.Should().BeNull();
        }

        private PlanItemStore CreateStore(
            Guid? caseDefId = null,
            PlanItemDefinition piDef = null,
            Interfaces.Model.PlanItem def = null,
            PlanItemState initialState = PlanItemState.Available)
        {
            caseDefId = caseDefId ?? Guid.NewGuid();
            piDef = piDef ?? new Milestone();
            def = def ?? new Interfaces.Model.PlanItem
            {
                DefinitionRef = piDef.Id
            };

            var store = new PlanItemStore();
            store.Apply(new Defined
            {
                CaseDefinitionId = caseDefId.Value,
                PlanItemDefinition = piDef,
                Definition = def
            });
            store.Apply(new Transitioned
            {
                Destination = initialState
            });
            return store;
        }
    }
}
