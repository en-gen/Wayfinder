using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.CmmnElement;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.PlanItem.Definitions;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Flow.Grains.Services.PlanItemBehaviorConfigurator;
using Flow.Grains.Services.PlanItemStateMachineConfigurator;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;
using Stateless;

namespace Flow.Grains.Plan.PlanItem
{
    public class PlanItemGrain :
        CmmnElementGrain<PlanItemStore, Interfaces.Model.PlanItem>,
        IPlanItemGrain,
        IBehaviorHost
    {
        private IPlanItemBehaviorConfigurator BehaviorConfigurator { get; }
        private IPlanItemStateMachineConfigurator StateMachineConfigurator { get; }
        
        private IPlanItemBehavior _behavior;
        private IPlanItemStateMachine _stateMachine;
        
        private PlanItemDefinition PlanItemDefinition => TentativeState.PlanItemDefinition;
        
        #region BehaviorHost

        Guid IBehaviorHost.CaseInstanceId => _caseInstanceId;
        string IBehaviorHost.Scope => _scope;
        string IBehaviorHost.Id => _id;
        string IBehaviorHost.ParentId => _parentId;
        Interfaces.Model.PlanItem IBehaviorHost.Definition => Definition;
        PlanItemStore IBehaviorHost.State => TentativeState;
        IPlanItemStateMachine IBehaviorHost.StateMachine => _stateMachine;
        IGrainFactory IBehaviorHost.GrainFactory => GrainFactory;
        void IBehaviorHost.RaiseEvent<TEvent>(TEvent @event) => RaiseEvent(@event);
        Task IBehaviorHost.ConfirmEvents() => ConfirmEvents();
        Task IBehaviorHost.SubscribeTo<TEvent>(
            string eventSourceRef,
            Func<TEvent, StreamSequenceToken, Task> eventHandler,
            StreamFlags flags) => SubscribeTo(eventSourceRef, eventHandler, flags);

        void IBehaviorHost.LogWithContext(Action<ILogger> logAction) => LogWithContext(logAction);

        #endregion

        public PlanItemGrain(
            IPlanItemBehaviorConfigurator behaviorConfigurator,
            IPlanItemStateMachineConfigurator stateMachineConfigurator,
            ILogger<PlanItemGrain> logger) :
            base(logger)
        {
            BehaviorConfigurator = behaviorConfigurator;
            StateMachineConfigurator = stateMachineConfigurator;
        }

        public override async Task OnActivateAsync()
        {
            await base.OnActivateAsync();

            if (State.IsDefined)
            {
                Bootstrap();
                await _behavior.Activate();
            }
        }

        public override async Task Define(Guid caseDefinitionId, Interfaces.Model.PlanItem definition)
        {
            var planItemDefinition = await GrainFactory.GetGrain<IPlanItemDefinitionGraphGrain>(caseDefinitionId)
                .Find(_scope, definition.DefinitionRef);

            if(planItemDefinition == null) throw new InvalidOperationException($"definition {definition.DefinitionRef} not found");

            RaiseEvent(new Defined
            {
                CaseDefinitionId = caseDefinitionId,
                Definition = definition,
                PlanItemDefinition = planItemDefinition
            });
            await ConfirmEvents();
            
            Bootstrap();
            await _behavior.Define();
            await _behavior.Activate();
        }

        private void Bootstrap()
        {
            _stateMachine = StateMachineConfigurator.Configure(State);

            _stateMachine.Configure(PlanItemState.Completed)
                .OnEntryAsync(OnEnterTerminal);
            _stateMachine.Configure(PlanItemState.Terminated)
                .OnEntryAsync(OnEnterTerminal);
            _stateMachine.OnTransitionedAsync(HandleTransitioned);
            _stateMachine.OnUnhandledTriggerAsync(HandleUnhandledTrigger);

            LogWithContext(logger => logger.LogInformation("{PlanItemScope} {PlanItemId} initializing {BehaviorType} behavior",
                _scope,
                _id,
                PlanItemDefinition.GetType().Name));

            _behavior = BehaviorConfigurator.Configure(this, PlanItemDefinition);
        }

        public Task Trigger(PlanItemTransition transition) => _stateMachine?.FireAsync(transition) ?? Task.CompletedTask;
        public Task<PlanItemState> GetState() => Task.FromResult(TentativeState.PlanItemState);

        private Task OnEnterTerminal() => Task.WhenAll(Definition.ExitCriteria
            .Select(c => UnsubscribeFrom<SentrySatisfiedEvent>(c.SentryRef)));

        private async Task HandleTransitioned(StateMachine<PlanItemState, PlanItemTransition>.Transition transition)
        {
            LogWithContext(logger => logger.LogInformation(
                "PlanItem {ElementScope}.{ElementId} processed transition {PreviousState} × {StandardEvent} = {CurrentState}",
                _scope,
                _id,
                transition.Trigger,
                transition.Source,
                transition.Destination));

            RaiseEvent(new Transitioned
            {
                Source = transition.Source,
                Destination = transition.Destination,
                Trigger = transition.Trigger
            });
            await ConfirmEvents();

            await Publish(new PlanItemTransitionedEvent(
                _scope,
                _id,
                transition.Trigger,
                transition.Source,
                transition.Destination));
        }

        private Task HandleUnhandledTrigger(PlanItemState state, PlanItemTransition trigger)
        {
            LogWithContext(logger => logger.LogInformation(
                "PlanItem {ElementScope}.{ElementId} attempted invalid transition {CurrentState} × {StandardEvent} = [INVALID]",
                _scope,
                _id,
                trigger,
                state));

            return Task.CompletedTask;
        }
    }
}
