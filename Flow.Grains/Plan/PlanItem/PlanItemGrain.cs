using System;
using System.Threading.Tasks;
using AutoMapper;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Plan.CmmnElement;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.PlanItem.Definitions;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Services.PlanItemBehaviorConfigurator;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;

namespace Flow.Grains.Plan.PlanItem
{
    public class PlanItemGrain :
        CmmnElementGrain<PlanItemStore, Interfaces.Model.PlanItem>,
        IPlanItemGrain,
        IBehaviorHost
    {
        private IPlanItemBehaviorConfigurator BehaviorConfigurator { get; }
        private IMapper Mapper { get; }

        private IPlanItemBehavior _behavior;
        
        #region BehaviorHost

        Guid IBehaviorHost.CaseInstanceId => _caseInstanceId;
        string IBehaviorHost.Address => _address;
        string IBehaviorHost.Scope => _scope;
        string IBehaviorHost.ParentInstanceId => _parentId;
        string IBehaviorHost.InstanceId => _instanceId;
        string IBehaviorHost.DefinitionId => Definition?.Id;

        Interfaces.Model.PlanItem IBehaviorHost.Definition => Definition;
        PlanItemStore IBehaviorHost.State => TentativeState;
        IGrainFactory IBehaviorHost.GrainFactory => GrainFactory;

        void IBehaviorHost.RaiseEvent<TEvent>(TEvent @event) => RaiseEvent(@event);
        Task IBehaviorHost.ConfirmEvents() => ConfirmEvents();
        Task IBehaviorHost.SubscribeTo<TEvent>(
            string eventSourceId,
            Func<TEvent, StreamSequenceToken, Task> eventHandler,
            StreamFlags flags) => SubscribeTo(eventSourceId, eventHandler, flags);
        Task IBehaviorHost.UnsubscribeFrom<TEvent>(string eventSourceId) => UnsubscribeFrom<TEvent>(eventSourceId);
        Task IBehaviorHost.Publish<TEvent>(TEvent @event) => PublishEvent(@event);

        void IBehaviorHost.LogWithContext(Action<ILogger> logAction) => LogWithContext(logAction);

        #endregion

        public PlanItemGrain(
            IPlanItemBehaviorConfigurator behaviorConfigurator,
            IMapper mapper,
            ILogger<PlanItemGrain> logger) :
            base(logger)
        {
            BehaviorConfigurator = behaviorConfigurator;
            Mapper = mapper;
        }

        public override async Task OnActivateAsync()
        {
            await base.OnActivateAsync();

            if (State.Defined)
            {
                _behavior = await BehaviorConfigurator.Configure(this, State.PlanItemDefinition);
            }
        }

        public override Task Define(Guid caseDefinitionId, Interfaces.Model.PlanItem definition) =>
            DefineRepetition(caseDefinitionId, definition, 0);

        public async Task DefineRepetition(Guid caseDefinitionId, Interfaces.Model.PlanItem definition, int repetition)
        {
            var planItemDefinition = await GrainFactory.GetGrain<IPlanItemDefinitionGraphGrain>(caseDefinitionId)
                .Find(_scope, definition.DefinitionRef);

            if (planItemDefinition == null) throw new InvalidOperationException($"definition {definition.DefinitionRef} not registered");

            RaiseEvent(new Defined
            {
                CaseDefinitionId = caseDefinitionId,
                Repetition = repetition,
                Definition = definition,
                PlanItemDefinition = planItemDefinition
            });

            _logContext["CaseDefinitionId"] = caseDefinitionId;
            _logContext["ElementDefinitionId"] = definition.Id;

            await ConfirmEvents();

            _behavior = await BehaviorConfigurator.Configure(this, State.PlanItemDefinition);
        }

        public async Task<PlanItemSnapshot> Trigger(PlanItemTransition transition)
        {
            if(_behavior == null) throw new InvalidOperationException("attempted Trigger on Uninitialized PlanItem");

            await _behavior.Trigger(transition);
            var ret = await GetSnapshot();
            return ret;
        }

        public Task<PlanItemSnapshot> GetSnapshot() => Task.FromResult(Mapper.Map<PlanItemSnapshot>(this));
    }
}
