using System;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.Case.Events;
using Wayfinder.Grains.Plan.CmmnElement;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Plan.PlanItem.Behaviors.Stores;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.Case
{
    [GenerateSerializer]
    public class CaseStore : CmmnElementStore<Interfaces.Model.Case>, IBehaviorStore
    {
        [Id(0)]
        public PlanItemDefinition PlanItemDefinition { get; private set; }

        // ADO #33 - the owning tenant, projected from CaseCreated.TenantId. CaseGrain.Trigger/
        // GetSnapshot compare this against CaseRequestContext.TenantId to enforce cross-tenant
        // isolation at the case's public surface. Events persisted before this change replay with
        // TenantId == Guid.Empty (a legacy/empty owning-tenant), which will not match any real
        // tenant - such cases become inaccessible rather than cross-accessible (acceptable
        // pre-production; no migration performed).
        [Id(15)]
        public Guid TenantId { get; private set; }

        [Id(1)]
        public bool UserCompletable { get; private set; }

        [Id(2)]
        public bool Required { get; private set; }
        [Id(3)]
        public string RequiredEvaluationError { get; private set; }

        [Id(4)]
        public bool Repeatable { get; private set; }
        [Id(5)]
        public string RepeatableEvaluationError { get; private set; }
        [Id(6)]
        public bool Repeated { get; private set; }
        [Id(7)]
        public int Repetition { get; private set; }

        [Id(8)]
        public bool ManuallyActivatable { get; private set; }
        [Id(9)]
        public string ManuallyActivatableEvaluationError { get; private set; }

        [Id(10)]
        public PlanItemState PlanItemState { get; private set; }
        [Id(11)]
        public PlanItemState? ParentSuspendState { get; private set; }

        [Id(12)]
        public CriterionStore EntryCriterionStore { get; } = new CriterionStore();
        [Id(13)]
        public CriterionStore ExitCriterionStore { get; } = new CriterionStore();

        [Id(14)]
        public object BehaviorExtension { get; private set; }

        public void Apply(CaseCreated @event)
        {
            base.Apply(@event);
            PlanItemDefinition = @event.Definition.CasePlanModel;
            Repetition = @event.Repetition;
            TenantId = @event.TenantId;

            BehaviorExtension = new StageBehaviorStore();
        }

        public void Apply(Transitioned @event)
        {
            PlanItemState = @event.Destination;
            Updated = @event.Updated;
        }

        public void Apply(EntryCriterionSatisfied @event)
        {
            EntryCriterionStore.Apply(@event);
            Updated = @event.Updated;
        }

        public void Apply(ExitCriterionSatisfied @event)
        {
            ExitCriterionStore.Apply(@event);
            Updated = @event.Updated;
        }

        public void Apply(RequiredRuleEvaluated @event)
        {
            Updated = @event.Updated;

            Required = @event.Result;
            RequiredEvaluationError = @event.Error;
        }

        public void Apply(RepetitionRuleEvaluated @event)
        {
            Updated = @event.Updated;

            // 8.6.4 - the first (Create -> Available) evaluation's result MUST be discarded, not
            // treated as the item's repeatable determination - see RepetitionRuleEvaluated.Discard.
            if (@event.Discard) return;

            Repeatable = @event.Result;
            RepeatableEvaluationError = @event.Error;
        }

        public void Apply(ManualActivationRuleEvaluated @event)
        {
            Updated = @event.Updated;

            ManuallyActivatable = @event.Result;
            ManuallyActivatableEvaluationError = @event.Error;
        }

        public void Apply(ParentSuspended @event)
        {
            ParentSuspendState = PlanItemState;
            Updated = @event.Updated;
        }

        public void Apply(ParentResumed @event)
        {
            ParentSuspendState = null;
            Updated = @event.Updated;
        }

        public void Apply(UserCompletableCriteriaMet @event)
        {
            Updated = @event.Updated;
            UserCompletable = @event.UserCompletable;
        }

        public void Apply(Repeated @event)
        {
            Updated = @event.Updated;
            Repeated = true;
        }

        public void Apply(ChildCreated @event)
        {
            Updated = @event.Updated;

            if (BehaviorExtension is StageBehaviorStore stageStore)
            {
                stageStore.Apply(@event);
            }
        }
    }
}
