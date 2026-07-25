using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wayfinder.Grains.Infrastructure.Mapping;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.PlanItem;
using Wayfinder.Grains.Plan.Case;
using Wayfinder.Grains.Plan.CmmnElement;
using Wayfinder.Grains.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Services.PlanItemBehaviorConfigurator;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;

namespace Wayfinder.Grains.Plan.PlanItem
{
    public class PlanItemGrain :
        CmmnElementGrain<PlanItemStore, Interfaces.Model.PlanItem>,
        IPlanItemInternalGrain,
        IBehaviorHost
    {
        private readonly IPlanItemBehaviorConfigurator _behaviorConfigurator;

        private IPlanItemBehavior _behavior;

        #region BehaviorHost

        Guid IBehaviorHost.CaseInstanceId => _caseInstanceId;
        string IBehaviorHost.Address => _address;
        string IBehaviorHost.Scope => _scope;
        string IBehaviorHost.ParentInstanceId => _parentId;
        string IBehaviorHost.ParentDefinitionId => State.ParentDefinitionId;
        string IBehaviorHost.InstanceId => _instanceId;
        string IBehaviorHost.DefinitionId => Definition?.Id;
        // My own full DEFINITION-scope path, for MY children to search their definition-index
        // lookup with: my parent's definition-scope (State.ParentDefinitionScope, threaded in via
        // DefineRepetition) plus my own DEFINITION's id - mirrors DefinitionGraphNode.Address's
        // Scope+Id composition exactly, so this always equals the DefinitionIndex key prefix
        // CaseDefinitionGrain.CreateStageDefinitions built for my own definition node (#65).
        // NOT Definition.Id - that is the containing <planItem> element's OWN id (e.g.
        // "PlanItemStageA"; correct for DefinitionId/PublishEvent's stream key above), a
        // different id namespace than the definitionRef target CaseDefinitionGrain indexed (e.g.
        // "StageA"). State.PlanItemDefinition is that resolved definitionRef target - the same
        // object DefineRepetition got back from GetPlanItemDefinition - so its own .Id is what
        // CreateStageDefinitions actually used as this definition node's DefinitionGraphNode.Id.
        string IBehaviorHost.DefinitionScope =>
            string.IsNullOrEmpty(State.ParentDefinitionScope)
                ? State.PlanItemDefinition?.Id
                : $"{State.ParentDefinitionScope}.{State.PlanItemDefinition?.Id}";

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

        // parentDefinitionId/parentDefinitionScope: null here (as opposed to StageBehavior.
        // CreateChild's threaded Host.DefinitionId/Host.DefinitionScope) - this bare 2-arg
        // overload has no route to its caller's parent context. Its only production caller is
        // this same override; anything invoking it directly (e.g. test scaffolding) accepts that
        // the resulting instance's parent-cascade subscription is skipped (BaseBehavior.Activate's
        // root guard, #63) and that its definition lookup falls back to the legacy instance-scope
        // resolution, which only resolves casePlanModel-root declarations (#65).
        public override Task Define(string caseDefinitionId, Interfaces.Model.PlanItem definition) =>
            DefineRepetition(caseDefinitionId, definition, 0, null, null);

        public async Task DefineRepetition(string caseDefinitionId, Interfaces.Model.PlanItem definition, int repetition, string parentDefinitionId, string parentDefinitionScope)
        {
            // #65: CaseDefinitionGrain.DefinitionIndex is keyed on DEFINITION-id paths
            // (CaseDefinitionGrain.CreateStageDefinitions), so the lookup must be searched with
            // the same shape - parentDefinitionScope (threaded from Host.DefinitionScope via
            // StageBehavior.CreateChild), not this grain's own _scope (a runtime INSTANCE-id path
            // built from freshly-minted child GUIDs - StageBehavior.CreateChild's childInstanceId
            // - that only coincidentally matches at the casePlanModel root). Falls back to _scope
            // for the parentless bare-2-arg Define() path above, preserving its existing
            // root-level-only resolution.
            var planItemDefinition = await GrainFactory.GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .GetPlanItemDefinition(parentDefinitionScope ?? _scope, definition.DefinitionRef);

            if (planItemDefinition == null) throw new InvalidOperationException($"definition {definition.DefinitionRef} not registered");

            RaiseEvent(new Defined
            {
                CaseDefinitionId = caseDefinitionId,
                Repetition = repetition,
                Definition = definition,
                PlanItemDefinition = planItemDefinition,
                ParentDefinitionId = parentDefinitionId,
                ParentDefinitionScope = parentDefinitionScope
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

            _behavior = await _behaviorConfigurator.Configure(this, State.PlanItemDefinition);
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
