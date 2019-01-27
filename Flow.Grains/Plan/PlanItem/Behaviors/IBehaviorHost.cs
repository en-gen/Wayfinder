using System;
using System.Threading.Tasks;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    public interface IBehaviorHost
    {
        Guid CaseInstanceId { get; }
        string Scope { get; }
        string Id { get; }
        string ParentId { get; }
        
        Interfaces.Model.PlanItem Definition { get; }
        PlanItemStore State { get; }
        IPlanItemStateMachine StateMachine { get; }

        IGrainFactory GrainFactory { get; }

        void RaiseEvent<TEvent>(TEvent @event);
        Task ConfirmEvents();
        Task SubscribeTo<TEvent>(
            string eventSourceRef,
            Func<TEvent, StreamSequenceToken, Task> eventHandler,
            StreamFlags flags);

        void LogWithContext(Action<ILogger> logAction);
    }
}
