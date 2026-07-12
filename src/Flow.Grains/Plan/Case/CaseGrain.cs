using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flow.Grains.Infrastructure.Mapping;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.Case;
using Flow.Grains.Plan.Case.Events;
using Flow.Grains.Plan.CmmnElement;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.Role;
using Flow.Grains.Services.PlanItemBehaviorConfigurator;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;

namespace Flow.Grains.Plan.Case
{
    public class CaseGrain :
        CmmnElementGrain<CaseStore, Interfaces.Model.Case>,
        ICaseGrain,
        IBehaviorHost
    {
        private readonly IPlanItemBehaviorConfigurator _behaviorConfigurator;

        private IPlanItemBehavior _casePlanModel;

        #region BehaviorHost

        Guid IBehaviorHost.CaseInstanceId => _caseInstanceId;
        string IBehaviorHost.Address => _address;
        string IBehaviorHost.Scope => _scope;
        string IBehaviorHost.ParentInstanceId => _parentId;
        // The CasePlanModel root has no parent - BaseBehavior.Activate's root guard (#63) skips
        // the parent-transition subscription entirely when this is null/empty.
        string IBehaviorHost.ParentDefinitionId => null;
        string IBehaviorHost.InstanceId => _instanceId;
        string IBehaviorHost.DefinitionId => Definition?.Id;
        // NOT Definition.Id - that is the CASE's own id (correct for DefinitionId/PublishEvent's
        // stream key above, per #63, but a different id namespace entirely). The definition-scope
        // path CaseDefinitionGrain.DefinitionIndex is keyed on is rooted at the casePlanModel's
        // OWN id (CaseDefinitionGrain.Define: new DefinitionGraphNode(definition.CasePlanModel.Id)),
        // which for the root has no ancestor, so this IS the full definition-scope path (matches
        // DefinitionGraphNode.Address: Scope is empty for a node with no Parent, so Address == Id)
        // (#65).
        string IBehaviorHost.DefinitionScope => Definition?.CasePlanModel?.Id;

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

        public CaseGrain(
            IPlanItemBehaviorConfigurator behaviorConfigurator,
            ILogger<CaseGrain> logger) :
            base(logger)
        {
            _behaviorConfigurator = behaviorConfigurator ?? throw new ArgumentNullException(nameof(behaviorConfigurator));
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            await base.OnActivateAsync(cancellationToken);

            if (State.Defined)
            {
                await PostDefine();
            }
        }

        public async Task Create(string caseDefinitionId)
        {
            var caseDefinitionGrain = GrainFactory.GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId);

            if(!await caseDefinitionGrain.Defined()) throw new InvalidOperationException($"case definition {caseDefinitionId} not found");
            if(State.Defined) throw new InvalidOperationException($"case {_caseInstanceId} has already been created");

            var @case = await caseDefinitionGrain.GetDefinition();
            @case.CasePlanModel.IsCasePlanModel = true;

            RaiseEvent(new CaseCreated
            {
                CaseDefinitionId = @case.Id,
                Definition = @case
            });

            // PostDefine() (below) reads State.CaseDefinitionId/State.Definition - the confirmed
            // projection, not TentativeState - so the CaseCreated event raised above must be
            // confirmed first or both reads NRE (State.Definition is still null pre-confirmation).
            // Matches the established RaiseEvent-then-ConfirmEvents-then-PostDefine sequencing
            // PlanItemGrain.DefineRepetition already uses for the equivalent Defined event.
            await ConfirmEvents();

            await Task.WhenAll(@case.CaseRoles.Roles
                .Select(x => GrainFactory.GetGrain<IRoleGrain>(_caseInstanceId, x.Id)
                    .Define(caseDefinitionId, x)));

            await PostDefine();
        }

        private async Task PostDefine()
        {
            LogContext["CaseDefinitionId"] = State.CaseDefinitionId;
            LogContext["ElementDefinitionId"] = Definition.Id;
            LogContext["ElementScope"] = _scope;
            LogContext["ElementInstanceId"] = _instanceId;

            _casePlanModel = await _behaviorConfigurator.Configure(this, State.Definition.CasePlanModel);
        }

        public Task<CaseSnapshot> GetSnapshot() => Task.FromResult(State.ToSnapshot());

        public async Task<CaseSnapshot> Trigger(PlanItemTransition transition)
        {
            if (_casePlanModel == null) throw new InvalidOperationException("attempted Trigger on Uninitialized Case");

            // 8.4.1/Table 8.5 - Closed: "Terminal state. In this state no new activity is allowed
            // in the Case." PlanItemStateMachine.ConfigureForCasePlanModel already has no outgoing
            // Permit(...) edges from Closed, so an unhandled trigger would already silently no-op
            // via BaseBehavior.HandleUnhandledTrigger's log-only handler - this guard turns that
            // implicit immutability into an explicit, observable failure at the Case's own public
            // surface instead of a silent success-shaped no-op.
            if (State.PlanItemState == PlanItemState.Closed)
            {
                throw new InvalidOperationException($"case {_caseInstanceId} is Closed; no further transitions are allowed");
            }

            await _casePlanModel.Trigger(transition);
            return await GetSnapshot();
        }
    }
}
