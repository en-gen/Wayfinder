using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flow.Grains.Infrastructure.Mapping;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Plan.Case;
using Flow.Grains.Plan.CmmnElement;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Services.PlanItemBehaviorConfigurator;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;

namespace Flow.Grains.Plan.PlanItem
{
    public class PlanItemGrain :
        CmmnElementGrain<PlanItemStore, Interfaces.Model.PlanItem>,
        IPlanItemInternalGrain,
        IBehaviorHost
    {
        private IPlanItemBehaviorConfigurator BehaviorConfigurator { get; }

        private IPlanItemBehavior _behavior;
        
        #region BehaviorHost

        Guid IBehaviorHost.CaseInstanceId => _caseInstanceId;
        string IBehaviorHost.Address => _address;
        string IBehaviorHost.Scope => _scope;
        string IBehaviorHost.ParentInstanceId => _parentId;
        string IBehaviorHost.InstanceId => _instanceId;
        string IBehaviorHost.DefinitionId => Definition?.Id;

        IBehaviorDefinition IBehaviorHost.Definition => Definition;
        IBehaviorStore IBehaviorHost.State => TentativeState;
        IGrainFactory IBehaviorHost.GrainFactory => GrainFactory;

        void IBehaviorHost.RaiseEvent<TEvent>(TEvent @event) => RaiseEvent(@event);
        Task IBehaviorHost.ConfirmEvents() => ConfirmEvents();

        Task IBehaviorHost.SubscribeTo<TEvent>(
            string eventSourceId,
            Func<TEvent, StreamSequenceToken, Task> eventHandler,
            StreamFlags flags) => SubscribeTo(eventSourceId, eventHandler, flags);
        Task IBehaviorHost.UnsubscribeFrom<TEvent>(string eventSourceId) => UnsubscribeFrom<TEvent>(eventSourceId);
        Task IBehaviorHost.Publish<TEvent>(TEvent @event) => PublishEvent(@event);

        IDictionary<string, object> IBehaviorHost.Context => LogContext;
        void IBehaviorHost.LogWithContext(Action<ILogger> logAction) => LogWithContext(logAction);

        #endregion

        public PlanItemGrain(
            IPlanItemBehaviorConfigurator behaviorConfigurator,
            ILogger<PlanItemGrain> logger) :
            base(logger)
        {
            BehaviorConfigurator = behaviorConfigurator;
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            await base.OnActivateAsync(cancellationToken);

            if (State.Defined)
            {
                await PostDefine();
            }
        }

        public override Task Define(string caseDefinitionId, Interfaces.Model.PlanItem definition) =>
            DefineRepetition(caseDefinitionId, definition, 0);
        
        public async Task DefineRepetition(string caseDefinitionId, Interfaces.Model.PlanItem definition, int repetition)
        {
            var planItemDefinition = await GrainFactory.GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .GetPlanItemDefinition(_scope, definition.DefinitionRef);

            if (planItemDefinition == null) throw new InvalidOperationException($"definition {definition.DefinitionRef} not registered");

            RaiseEvent(new Defined
            {
                CaseDefinitionId = caseDefinitionId,
                Repetition = repetition,
                Definition = definition,
                PlanItemDefinition = planItemDefinition
            });

            await ConfirmEvents();

            await PostDefine();
        }

        private async Task PostDefine()
        {
            LogContext["CaseDefinitionId"] = State.CaseDefinitionId;
            LogContext["ElementDefinitionId"] = Definition.Id;
            LogContext["PlanItemDefinition"] = State.PlanItemDefinition.GetType().Name;
            LogContext["ElementScope"] = _scope;
            LogContext["ElementInstanceId"] = _instanceId;

            _behavior = await BehaviorConfigurator.Configure(this, State.PlanItemDefinition);
        }

        public Task<PlanItemSnapshot> GetSnapshot() => Task.FromResult(State.ToSnapshot());

        public async Task<PlanItemSnapshot> Trigger(PlanItemTransition transition)
        {
            if (_behavior == null) throw new InvalidOperationException("attempted Trigger on Uninitialized PlanItem");

            await _behavior.Trigger(transition);
            return await GetSnapshot();
        }
    }
}
