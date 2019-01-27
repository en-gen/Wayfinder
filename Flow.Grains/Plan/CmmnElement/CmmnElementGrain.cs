using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Infrastructure.Extensions;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.EventSourcing;
using Orleans.Providers;
using Orleans.Streams;

namespace Flow.Grains.Plan.CmmnElement
{
    [LogConsistencyProvider(ProviderName = "LogStorage")]
    public abstract class CmmnElementGrain<TState, TDefinition> :
        JournaledGrain<TState>,
        ICmmnElementActor<TDefinition>
        where TState : CmmnElementStore<TDefinition>, new()
        where TDefinition : Interfaces.Model.CmmnElement
    {
        protected Guid _caseInstanceId;
        
        protected string _scope;
        protected string _parentId;
        protected string _id;

        private ILogger Logger { get; }

        protected TDefinition Definition => TentativeState.Definition ?? throw new Exception("Definition is not initialized");
        
        protected CmmnElementGrain(ILogger logger)
        {
            Logger = logger;
        }

        public override Task OnActivateAsync()
        {
            _caseInstanceId = this.GetPrimaryKey(out var id);
            _scope = id.Substring(0, id.LastIndexOf('.'));
            var chunks = id.Split('.').ToArray();
            _parentId = chunks[Math.Max(0, chunks.Length - 2)];
            _id = chunks[chunks.Length - 1];

            return base.OnActivateAsync();
        }

        public virtual Task Define(Guid caseDefinitionId, TDefinition definition)
        {
            if(_id != definition.Id) throw new ArgumentException(nameof(definition), $"{nameof(definition)}.{nameof(definition.Id)} must equal grain address id {_id}");

            RaiseEvent(new CmmnElementDefined<TDefinition>
            {
                CaseDefinitionId = caseDefinitionId,
                Definition = definition
            });
            return ConfirmEvents();
        }
        
        protected IAsyncStream<TEvent> GetCaseEventStream<TEvent>(string eventSourceRef) =>
            GetStreamProvider("Default").GetCaseEventStream<TEvent>(_caseInstanceId, eventSourceRef);

        protected async Task SubscribeTo<TEvent>(
            string eventSourceRef,
            Func<TEvent, StreamSequenceToken, Task> eventHandler,
            StreamFlags flags)
        {
            var stream = GetCaseEventStream<TEvent>(eventSourceRef);
            var handles = await stream.GetAllSubscriptionHandles();

            var resuming = (flags & StreamFlags.Resume) == StreamFlags.Resume && handles.Any();

            LogWithContext(logger => logger.LogInformation($"{{ElementType}} {{ElementScope}}.{{ElementId}}: {(resuming ? "resuming" : "creating")} {{EventType}} subscription to {{EventSourceRef}}",
                Definition.GetType().Name,
                _scope,
                Definition.Id,
                typeof(TEvent).Name,
                eventSourceRef));

            if (resuming)
            {
                await Task.WhenAll(handles.Select(handle => handle.ResumeAsync(eventHandler)));
            }
            else if ((flags & StreamFlags.Create) == StreamFlags.Create)
            {
                await stream.SubscribeAsync(eventHandler);
            }
        }

        protected async Task UnsubscribeFrom<TEvent>(string eventSourceRef)
        {
            LogWithContext(logger => logger.LogInformation(
                "{ElementType} {ElementScope}.{ElementId}: removing {EventType} subscription from {EventSourceRef}",
                Definition.GetType().Name,
                _scope,
                Definition.Id,
                typeof(TEvent).Name,
                eventSourceRef));

            var handles = await GetCaseEventStream<TEvent>(eventSourceRef).GetAllSubscriptionHandles();
            await Task.WhenAll(handles.Select(handle => handle.UnsubscribeAsync()));
        }

        protected Task Publish<TEvent>(TEvent @event)
            where TEvent : BaseEvent =>
            GetCaseEventStream<TEvent>(Definition.Id)
                .OnNextAsync(@event);

        protected virtual void LogWithContext(Action<ILogger> logAction)
        {
            using(Logger.BeginScope(new Dictionary<string, object>
            {
                ["CaseDefinitionId"] = TentativeState.CaseDefinitionId,
                ["CaseInstanceId"] = _caseInstanceId,
                ["ElementType"] = TentativeState.Definition?.GetType().Name,
                ["ElementScope"] = _scope,
                ["ElementId"] = TentativeState.Definition?.Id,
            }))
            {
                logAction?.Invoke(Logger);
            }
        }
    }
}
