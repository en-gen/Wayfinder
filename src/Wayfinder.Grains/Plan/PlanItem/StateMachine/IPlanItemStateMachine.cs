using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;
using Stateless;
using Stateless.Reflection;

namespace Wayfinder.Grains.Plan.PlanItem.StateMachine
{
    public interface IPlanItemStateMachine
    {
        PlanItemState? ParentSuspendState { get; }
        PlanItemState State { get; }
        IEnumerable<PlanItemTransition> PermittedTriggers { get; }
        Task ActivateAsync();
        Task DeactivateAsync();
        Task FireAsync(PlanItemTransition trigger);

        // D10 - carries "WHICH ExitCriterion drove this Exit" from the firing site
        // (StageBehavior/TaskBehavior.HandleSentrySatisfied's ExitCriterion branch) through to
        // BaseBehavior.HandleTransitioned's OnTransitionedAsync callback, so the
        // PlanItemTransitionedEvent it raises can finally carry a real ExitCriterionRef - see
        // PlanItemStateMachine's implementation and SentryGrain's class remarks. Only meaningful
        // for PlanItemTransition.Exit today; callers pass any other trigger at their own risk (see
        // the implementation's guard).
        Task FireAsync(PlanItemTransition trigger, string exitCriterionRef);
        void OnUnhandledTriggerAsync(Func<PlanItemState, PlanItemTransition, Task> unhandledTriggerAction);
        void OnUnhandledTriggerAsync(Func<PlanItemState, PlanItemTransition, ICollection<string>, Task> unhandledTriggerAction);
        void OnTransitionedAsync(Func<StateMachine<PlanItemState, PlanItemTransition>.Transition, Task> onTransitionAction);
        IEnumerable<PlanItemTransition> GetPermittedTriggers(params object[] args);
        StateMachineInfo GetInfo();
        StateMachine<PlanItemState, PlanItemTransition>.StateConfiguration Configure(PlanItemState state);
        bool IsInState(PlanItemState state);
        bool CanFire(PlanItemTransition trigger);
        string ToString();
    }
}
