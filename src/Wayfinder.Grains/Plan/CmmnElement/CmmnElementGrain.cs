using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Plan.CmmnElementGrain;
using Flow.Grains.Plan.CmmnElement.Events;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.EventSourcing;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Streams;

namespace Flow.Grains.Plan.CmmnElement
{
    [LogConsistencyProvider(ProviderName = "LogStorage")]
    public abstract class CmmnElementGrain<TState, TDefinition> :
        JournaledGrain<TState>,
        ICmmnElementGrain<TDefinition>
        where TState : CmmnElementStore<TDefinition>, new()
        where TDefinition : Interfaces.Model.CmmnElement
    {
        protected Guid _caseInstanceId;

        protected string _address;
        protected string _scope;
        protected string _parentId;
        protected string _instanceId;

        protected TDefinition Definition => TentativeState.Definition ?? throw new Exception("Definition is not initialized");

        private readonly ILogger _logger;
        protected IDictionary<string, object> LogContext { get; }

        protected CmmnElementGrain(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            LogContext = new Dictionary<string, object>();
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _caseInstanceId = this.GetPrimaryKey(out _address);

            var chunks = _address.Split('.');
            _scope = chunks.Length > 0
                ? string.Join('.', chunks.Take(chunks.Length - 1))
                : string.Empty;
            _instanceId = chunks.LastOrDefault();
            _parentId = chunks.Length > 1
                ? chunks.Skip(chunks.Length - 2).FirstOrDefault()
                : string.Empty;

            // Log-context enrichment only - not the tenant-enforcement boundary (that lives at
            // request-handling grain methods, e.g. CaseGrain.GetSnapshot, via
            // CrossTenantAccessException). Activation can be triggered by a background stream
            // delivery or reminder with no caller-initiated RequestContext propagated, so these
            // must not throw the way CaseRequestContext.TenantId/UserId do.
            LogContext["TenantId"] = CaseRequestContext.TenantIdOrNull;
            LogContext["UserId"] = CaseRequestContext.UserIdOrNull;
            LogContext["@UserRoles"] = CaseRequestContext.UserRoles;
            LogContext["CorrelationId"] = System.Diagnostics.Activity.Current?.Id;
            LogContext["CaseInstanceId"] = _caseInstanceId;
            LogContext["ElementAddress"] = _address;
            LogContext["ElementScope"] = _scope;
            LogContext["ElementParentId"] = _parentId;
            LogContext["ElementInstanceId"] = _instanceId;
            LogContext["Element"] = typeof(TDefinition).Name;

            await base.OnActivateAsync(cancellationToken);

            if (TentativeState.Defined)
            {
                LogContext["CaseDefinitionId"] = TentativeState.CaseDefinitionId;
                LogContext["ElementDefinitionId"] = TentativeState.Definition.Id;
            }
        }

        public Task<bool> Defined() => Task.FromResult(State.Defined);

        // ADO #59 - shadows (does not override - JournaledGrain<TState>.RaiseEvent<TEvent> is a
        // plain, non-virtual generic method) the Orleans base RaiseEvent for every unqualified
        // RaiseEvent(...) call made from within this class or any subclass (CaseGrain,
        // CaseFileItemGrain, PlanItemGrain, PlanningTableGrain, RoleGrain, SentryGrain, and every
        // PlanItem Behavior via IBehaviorHost.RaiseEvent - see e.g. BaseBehavior/StageBehavior -
        // which all delegate to this same unqualified call, resolved at compile time to whichever
        // RaiseEvent is visible from the calling class's own hierarchy). This is the single append
        // point for every journaled event this grain hierarchy raises, so stamping here covers all
        // of them automatically - no per-event-type or per-call-site stamping code, and no future
        // event type can forget it just by existing.
        protected new void RaiseEvent<TEvent>(TEvent @event)
        {
            ActorStamping.Apply(@event);
            base.RaiseEvent(@event);
        }

        public virtual Task Define(string caseDefinitionId, TDefinition definition)
        {
            RaiseEvent(new CmmnElementDefined<TDefinition>
            {
                CaseDefinitionId = caseDefinitionId,
                Definition = definition
            });

            LogContext["CaseDefinitionId"] = caseDefinitionId;
            LogContext["ElementDefinitionId"] = definition.Id;

            return ConfirmEvents();
        }

        // ADO #59 - minimal read-back seam over Orleans's own JournaledGrain.RetrieveConfirmedEvents,
        // added so this work item's replay-safety tests can prove actor stamping survives a real
        // RaiseEvent/ConfirmEvents/serialize/persist round trip without reaching into grain
        // internals via reflection (Orleans TestCluster only exposes a grain's PUBLIC interface to
        // callers - there is no other way to observe the raw journal from a test). Deliberately raw
        // (no paging/filtering) - #58 (case-file version history) is expected to want a richer,
        // curated equivalent; this is not that design, just today's smallest working seam.
        public Task<IReadOnlyList<object>> GetJournaledEvents() => RetrieveConfirmedEvents(0, Version);

        private IAsyncStream<TEvent> GetCaseEventStream<TEvent>(string eventSourceRef) =>
            this.GetStreamProvider("Default").GetCaseEventStream<TEvent>(_caseInstanceId, eventSourceRef);

        protected async Task SubscribeTo<TEvent>(
            string eventSourceId,
            Func<TEvent, StreamSequenceToken, Task> eventHandler,
            StreamFlags flags)
        {
            var stream = GetCaseEventStream<TEvent>(eventSourceId);
            var handles = await stream.GetAllSubscriptionHandles();

            var resuming = (flags & StreamFlags.Resume) == StreamFlags.Resume && handles.Any();
            var creating = (flags & StreamFlags.Create) == StreamFlags.Create && !handles.Any();

            if (creating || resuming)
            {
                LogWithContext(logger => logger.LogInformation(
                    $"{{Element}} {{ElementScope}}.{{ElementInstanceId}} | {(creating ? "creating" : "resuming")} {{EventType}} subscription to {{EventSourceDefinitionId}}",
                    Definition.GetType().Name,
                    _scope,
                    _instanceId,
                    typeof(TEvent).Name,
                    eventSourceId));
            }

            if (resuming)
            {
                await Task.WhenAll(handles.Select(handle => handle.ResumeAsync(eventHandler)));
            }
            else if (creating)
            {
                await stream.SubscribeAsync(eventHandler);
            }
        }

        protected async Task UnsubscribeFrom<TEvent>(string eventSourceRef)
        {
            LogWithContext(logger => logger.LogInformation(
                "{Element} {ElementScope}.{ElementInstanceId} | removing {EventType} subscription from {EventSourceDefinitionId}",
                Definition.GetType().Name,
                _scope,
                _instanceId,
                typeof(TEvent).Name,
                eventSourceRef));

            var handles = await GetCaseEventStream<TEvent>(eventSourceRef).GetAllSubscriptionHandles();
            await Task.WhenAll(handles.Select(handle => handle.UnsubscribeAsync()));
        }

        protected Task PublishEvent<TEvent>(TEvent @event) =>
            GetCaseEventStream<TEvent>(Definition.Id).OnNextAsync(@event);

        protected void LogWithContext(Action<ILogger> logAction)
        {
            using (_logger.BeginScope(LogContext))
            {
                logAction?.Invoke(_logger);
            }
        }
    }
}
