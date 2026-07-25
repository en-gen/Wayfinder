using System;
using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Microsoft.Extensions.Logging;
using Moq;

namespace Flow.Grains.Tests.Utils.Helpers
{
    public class MockPlanItemStateMachine : Mock<IPlanItemStateMachine>
    {
        private readonly IPlanItemStateMachine _stateMachine;

        public MockPlanItemStateMachine(IBehaviorStore store)
        {
            _stateMachine = new PlanItemStateMachine(store, Mock.Of<ILogger<PlanItemStateMachine>>());

            SetupGet(x => x.State)
                .Returns(() => _stateMachine.State);

            SetupGet(x => x.ParentSuspendState)
                .Returns(() => _stateMachine.ParentSuspendState);

            SetupGet(x => x.PermittedTriggers)
                .Returns(() => _stateMachine.PermittedTriggers);

            Setup(x => x.CanFire(It.IsAny<PlanItemTransition>()))
                .Returns<PlanItemTransition>(x => _stateMachine.CanFire(x));

            Setup(x => x.Configure(It.IsAny<PlanItemState>()))
                .Returns<PlanItemState>(x => _stateMachine.Configure(x));

            Setup(x => x.FireAsync(It.IsAny<PlanItemTransition>()))
                .Returns<PlanItemTransition>(x => _stateMachine.FireAsync(x));

            Setup(x => x.FireAsync(It.IsAny<PlanItemTransition>(), It.IsAny<string>()))
                .Returns<PlanItemTransition, string>((x, exitCriterionRef) => _stateMachine.FireAsync(x, exitCriterionRef));

            Setup(x => x.GetPermittedTriggers(It.IsAny<object[]>()))
                .Returns<object[]>(_stateMachine.GetPermittedTriggers);

            Setup(x => x.IsInState(It.IsAny<PlanItemState>()))
                .Returns<PlanItemState>(_stateMachine.IsInState);

            Setup(x => x.ActivateAsync())
                .Returns(_stateMachine.ActivateAsync);

            Setup(x => x.DeactivateAsync())
                .Returns(_stateMachine.DeactivateAsync);

            Setup(x => x.GetInfo())
                .Returns(_stateMachine.GetInfo);

            Setup(x => x.OnTransitionedAsync(It.IsAny<Func<PlanItemStateMachine.Transition, Task>>()))
                .Callback<Func<PlanItemStateMachine.Transition, Task>>(x => _stateMachine.OnTransitionedAsync(x));

            Setup(x => x.OnUnhandledTriggerAsync(It.IsAny<Func<PlanItemState, PlanItemTransition, Task>>()))
                .Callback<Func<PlanItemState, PlanItemTransition, Task>>(x => _stateMachine.OnUnhandledTriggerAsync(x));
        }
    }
}
