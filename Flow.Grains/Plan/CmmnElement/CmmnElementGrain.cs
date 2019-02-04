using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Infrastructure.Extensions;
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

        private ILogger Logger { get; }

        protected TDefinition Definition => TentativeState.Definition ?? throw new Exception("Definition is not initialized");
        
        protected readonly IDictionary<string, object> _logContext = new Dictionary<string, object>();

        protected CmmnElementGrain(ILogger logger)
        {
            Logger = logger;
        }

        public override async Task OnActivateAsync()
        {
            _caseInstanceId = this.GetPrimaryKey(out _address);
            
            var chunks = _address.Split('.');
            _scope = string.Join('.', chunks.Take(chunks.Length - 1));
            _instanceId = chunks.LastOrDefault();
            _parentId = chunks.Length > 1
                ? chunks.Skip(chunks.Length - 2).FirstOrDefault()
                : string.Empty;

            _logContext["CorrelationId"] = RequestContext.ActivityId;
            _logContext["CaseInstanceId"] = _caseInstanceId;
            _logContext["ElementAddress"] = _address;
            _logContext["ElementScope"] = _scope;
            _logContext["ElementParentId"] = _parentId;
            _logContext["ElementInstanceId"] = _instanceId;
            _logContext["Element"] = typeof(TDefinition).Name;
            
            await base.OnActivateAsync();

            if (TentativeState.Defined)
            {
                _logContext["CaseDefinitionId"] = TentativeState.CaseDefinitionId;
                _logContext["ElementDefinitionId"] = TentativeState.Definition.Id;
            }
        }

        public Task<bool> Defined() => Task.FromResult(State.Defined);

        public virtual Task Define(Guid caseDefinitionId, TDefinition definition)
        {
            RaiseEvent(new CmmnElementDefined<TDefinition>
            {
                CaseDefinitionId = caseDefinitionId,
                Definition = definition
            });
            
            _logContext["CaseDefinitionId"] = caseDefinitionId;
            _logContext["ElementDefinitionId"] = definition.Id;

            return ConfirmEvents();
        }
        
        private IAsyncStream<TEvent> GetCaseEventStream<TEvent>(string eventSourceRef) =>
            GetStreamProvider("Default").GetCaseEventStream<TEvent>(_caseInstanceId, eventSourceRef);

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
                    $"{{Element}} {{ElementScope}}.{{ElementInstanceId}}: {(creating ? "creating" : "resuming")} {{EventType}} subscription to {{EventSourceDefinitionId}}",
                    Definition.GetType().Name,
                    _scope,
                    Definition.Id,
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
                "{Element} {ElementScope}.{ElementInstanceId}: removing {EventType} subscription from {EventSourceDefinitionId}",
                Definition.GetType().Name,
                _scope,
                Definition.Id,
                typeof(TEvent).Name,
                eventSourceRef));

            var handles = await GetCaseEventStream<TEvent>(eventSourceRef).GetAllSubscriptionHandles();
            await Task.WhenAll(handles.Select(handle => handle.UnsubscribeAsync()));
        }

        protected Task PublishEvent<TEvent>(TEvent @event) =>
            GetCaseEventStream<TEvent>(Definition.Id).OnNextAsync(@event);

        protected void LogWithContext(Action<ILogger> logAction)
        {
            using(Logger.BeginScope(_logContext))
            {
                logAction?.Invoke(Logger);
            }
        }
    }
}
