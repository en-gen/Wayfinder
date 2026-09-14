using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wayfinder.Grains.Expressions;
using Wayfinder.Grains.Interfaces.Model;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;

namespace Wayfinder.Grains.Plan.PlanItem.Behaviors
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
        // The full dotted DEFINITION-id path from the casePlanModel root down to and including
        // this host's own definition id (e.g. "CPM.StageA") - the definition-tree counterpart to
        // Address/Scope above, which are runtime INSTANCE-id paths. CaseDefinitionGrain.
        // DefinitionIndex is keyed on exactly this shape (CaseDefinitionGrain.CreateStageDefinitions
        // builds it from PlanItemDefinition.Id, never from a runtime instance id), so a child
        // created under this host must look up its own definition using THIS value as the search
        // scope, not Address/Scope - the same instance-vs-definition confusion #63 fixed for the
        // parent-transition subscription, here in the definition-index lookup (#65).
        string DefinitionScope { get; }

        IBehaviorDefinition Definition { get; }
        IBehaviorStore State { get; }

        IGrainFactory GrainFactory { get; }

        // Expression evaluation with its context already bound by this host (design 05 section
        // D.2). Behaviors MUST go through this rather than reaching for GrainFactory: the
        // evaluator itself makes no outbound grain calls, and binding belongs to whoever already
        // holds the data - which after phase P2 is the case grain, locally.
        IExpressionContext Expressions { get; }

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
