using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Stateless;
using Stateless.Reflection;

namespace Flow.Grains.Plan.PlanItem.StateMachine
{
    public interface IPlanItemStateMachine
    {
        PlanItemState? ParentSuspendState { get; }
        PlanItemState State { get; }
        IEnumerable<PlanItemTransition> PermittedTriggers { get; }
        Task ActivateAsync();
        Task DeactivateAsync();
        Task FireAsync(PlanItemTransition trigger);
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