using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.Case.Events;
using Flow.Grains.Plan.CmmnElement;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Behaviors.Stores;
using Flow.Grains.Plan.PlanItem.Events;

namespace Flow.Grains.Plan.Case
{
    public class CaseStore : CmmnElementStore<Interfaces.Model.Case>, IBehaviorStore
    {
        public PlanItemDefinition PlanItemDefinition { get; private set; }

        public bool UserCompletable { get; private set; }

        public bool Required { get; private set; }
        public string RequiredEvaluationError { get; private set; }

        public bool Repeatable { get; private set; }
        public string RepeatableEvaluationError { get; private set; }
        public bool Repeated { get; private set; }
        public int Repetition { get; private set; }

        public bool ManuallyActivatable { get; private set; }
        public string ManuallyActivatableEvaluationError { get; private set; }

        public PlanItemState PlanItemState { get; private set; }
        public PlanItemState? ParentSuspendState { get; private set; }

        public CriterionStore EntryCriterionStore { get; } = new CriterionStore();
        public CriterionStore ExitCriterionStore { get; } = new CriterionStore();

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
