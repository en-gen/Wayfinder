using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.Case.Events;
using Flow.Grains.Plan.CmmnElement;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Behaviors.Stores;
using Flow.Grains.Plan.PlanItem.Events;
using Orleans;

namespace Flow.Grains.Plan.Case
{
    [GenerateSerializer]
    public class CaseStore : CmmnElementStore<Interfaces.Model.Case>, IBehaviorStore
    {
        [Id(0)]
        public PlanItemDefinition PlanItemDefinition { get; private set; }

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
