using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    public interface IBehaviorHost
    {
        Guid CaseInstanceId { get; }
        string ParentInstanceId { get; }
        string Address { get; }
        string Scope { get; }
        string InstanceId { get; }
        string DefinitionId { get; }
        
        Interfaces.Model.PlanItem Definition { get; }
        PlanItemStore State { get; }

        IGrainFactory GrainFactory { get; }

        void RaiseEvent<TEvent>(TEvent @event);
        Task ConfirmEvents();
        Task SubscribeTo<TEvent>(
            string eventSourceId,
            Func<TEvent, StreamSequenceToken, Task> eventHandler,
            StreamFlags flags);
        Task UnsubscribeFrom<TEvent>(string eventSourceId);
        Task Publish<TEvent>(TEvent @event);

        IDictionary<string, object> Context { get; }
        void LogWithContext(Action<ILogger> logAction);
    }
}
