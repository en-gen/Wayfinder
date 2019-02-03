using System;
using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Microsoft.Extensions.Logging;
using Moq;

namespace Flow.Grains.Tests.Helpers
{
    public class MockPlanItemStateMachine : Mock<IPlanItemStateMachine>
    {
        private IPlanItemStateMachine StateMachine { get; }

        public MockPlanItemStateMachine(PlanItemStore store)
        {
            StateMachine = new PlanItemStateMachine(store, Mock.Of<ILogger<PlanItemStateMachine>>());
            
            SetupGet(x => x.State)
                .Returns(() => StateMachine.State);

            SetupGet(x => x.ParentSuspendState)
                .Returns(() => StateMachine.ParentSuspendState);

            SetupGet(x => x.PermittedTriggers)
                .Returns(() => StateMachine.PermittedTriggers);

            Setup(x => x.CanFire(It.IsAny<PlanItemTransition>()))
                .Returns<PlanItemTransition>(x => StateMachine.CanFire(x));

            Setup(x => x.Configure(It.IsAny<PlanItemState>()))
                .Returns<PlanItemState>(x => StateMachine.Configure(x));

            Setup(x => x.FireAsync(It.IsAny<PlanItemTransition>()))
                .Returns<PlanItemTransition>(x => StateMachine.FireAsync(x));

            Setup(x => x.GetPermittedTriggers(It.IsAny<object[]>()))
                .Returns<object[]>(StateMachine.GetPermittedTriggers);

            Setup(x => x.IsInState(It.IsAny<PlanItemState>()))
                .Returns<PlanItemState>(StateMachine.IsInState);

            Setup(x => x.ActivateAsync())
                .Returns(StateMachine.ActivateAsync);

            Setup(x => x.DeactivateAsync())
                .Returns(StateMachine.DeactivateAsync);

            Setup(x => x.GetInfo())
                .Returns(StateMachine.GetInfo);

            Setup(x => x.OnTransitionedAsync(It.IsAny<Func<PlanItemStateMachine.Transition, Task>>()))
                .Callback<Func<PlanItemStateMachine.Transition, Task>>(x => StateMachine.OnTransitionedAsync(x));

            Setup(x => x.OnUnhandledTriggerAsync(It.IsAny<Func<PlanItemState, PlanItemTransition, Task>>()))
                .Callback<Func<PlanItemState, PlanItemTransition, Task>>(x => StateMachine.OnUnhandledTriggerAsync(x));
        }
    }
}
