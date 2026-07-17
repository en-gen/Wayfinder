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
        // ADO #66 - NOT the generic _scope (CmmnElementGrain.OnActivateAsync's "address minus its
        // last dot-segment"). That generic split assumes every element sits at least one level
        // below its own containing scope - true for every ordinary PlanItem/Sentry/CaseFileItem
        // instance (Address = "CPM.<instanceId>", Scope = "CPM", the parent Stage's own address),
        // but false for the Case root: its Address IS its scope-key with no instance-id suffix
        // ("CPM", zero dots), so the generic split degenerates _scope to "". StageBehavior.
        // HandleSentrySatisfied's "Host.Scope != @event.SourceScope" guard (5.4.5.1: a criterion's
        // Sentry MUST be contained by the Stage/PlanFragment that contains the PlanItem) needs
        // Host.Scope to mean "the scope my OWN criteria's sentries live in". For an ordinary
        // PlanItem that scope is its immediate parent Stage's address; for the CasePlanModel's own
        // criteria (Case.ExitCriteria, populated from CasePlanModel.ExitCriteria - see
        // CaseDefinitionGrain.Define) the sentries they reference are ones the CasePlanModel
        // defines itself (StageBehavior.Define, over PlanItemDefinition.Sentries), so the matching
        // scope is the CasePlanModel's OWN address - _address, not the empty _scope its generic
        // split produces. Fixed HERE, not in the shared split: every other CmmnElementGrain
        // subtype's Scope already means the right thing for its own use.
        string IBehaviorHost.Scope => _address;
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

            if (!await caseDefinitionGrain.Defined()) throw new InvalidOperationException($"case definition {caseDefinitionId} not found");
            if (State.Defined) throw new InvalidOperationException($"case {_caseInstanceId} has already been created");

            var @case = await caseDefinitionGrain.GetDefinition();
            @case.CasePlanModel.IsCasePlanModel = true;

            RaiseEvent(new CaseCreated
            {
                CaseDefinitionId = @case.Id,
                Definition = @case,
                TenantId = CaseRequestContext.TenantId
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

        public Task<CaseSnapshot> GetSnapshot()
        {
            // ADO #33 - defense in depth: a case is only cross-tenant-checkable once it exists
            // (State.Defined - see CaseStore.Apply(CaseCreated)). A never-created case must keep
            // returning the null-Definition snapshot untouched, under ANY tenant context, so that
            // signal stays indistinguishable from a foreign-tenant existing case at the app layer
            // (both become 404 upstream) - see CrossTenantAccessException's remarks.
            if (State.Defined && State.TenantId != CaseRequestContext.TenantId)
            {
                throw new CrossTenantAccessException();
            }

            return Task.FromResult(State.ToSnapshot());
        }

        public async Task<CaseSnapshot> Trigger(PlanItemTransition transition)
        {
            if (_casePlanModel == null) throw new InvalidOperationException("attempted Trigger on Uninitialized Case");

            // ADO #33 - cross-tenant guard runs BEFORE the Closed-state guard below: a foreign
            // caller must not be able to learn anything about the case's state (e.g. that it is
            // Closed) - see CrossTenantAccessException's remarks and GetSnapshot above.
            if (State.Defined && State.TenantId != CaseRequestContext.TenantId)
            {
                throw new CrossTenantAccessException();
            }

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
