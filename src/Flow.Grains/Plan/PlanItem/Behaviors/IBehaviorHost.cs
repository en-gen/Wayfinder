using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    public interface IBehaviorHost
    {
        Guid CaseInstanceId { get; }
        string ParentInstanceId { get; }
        // Definition id of the parent PlanItem/CasePlanModel that instantiated this one - not the
        // freshly-minted-per-instance ParentInstanceId above. Publishing is always keyed on the
        // publisher's definition id (CmmnElementGrain.PublishEvent), so the parent-transition
        // subscription (BaseBehavior.Activate) must be keyed on this, not ParentInstanceId, to
        // ever resolve to the same stream the parent actually publishes on (#63). Null/empty for
        // the CasePlanModel root, which has no parent to subscribe to.
        string ParentDefinitionId { get; }
        string Address { get; }
        string Scope { get; }
        string InstanceId { get; }
        string DefinitionId { get; }
        
        IBehaviorDefinition Definition { get; }
        IBehaviorStore State { get; }

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
