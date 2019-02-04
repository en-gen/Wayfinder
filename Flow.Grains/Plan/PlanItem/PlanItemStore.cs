using System;
using System.Collections.Generic;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Plan.CmmnElement;
using Flow.Grains.Plan.CmmnElement.Events;
using Flow.Grains.Plan.PlanItem.Events;

namespace Flow.Grains.Plan.PlanItem
{
    [Serializable]
    public class PlanItemStore : CmmnElementStore<Interfaces.Model.PlanItem>
    {
        public PlanItemDefinition PlanItemDefinition { get; private set; }

        public bool UserCompletable { get; private set; }
        public bool Required { get; private set; }
        public bool Repeated { get; private set; }

        public int Repetition { get; private set; }

        public PlanItemState PlanItemState { get; private set; }
        public PlanItemState? ParentSuspendState { get; private set; }

        public CriterionStore EntryCriterionStore { get; } = new CriterionStore();
        public CriterionStore ExitCriterionStore { get; } = new CriterionStore();

        // only applicable to PlanItems defined by a Stage
        // PlanItemDefinitionId => PlanItemInstanceId => Repetition
        public IDictionary<string, IDictionary<string, int>> Children { get; } = new Dictionary<string, IDictionary<string, int>>();

        public void Apply(BaseUpdate @event)
        {
            Updated = @event.Updated;
        }

        public void Apply(Defined @event)
        {
            base.Apply(@event);
            PlanItemDefinition = @event.PlanItemDefinition;
            Repetition = @event.Repetition;
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
            Required = @event.Result;
            Updated = @event.Updated;
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
            
            if (!Children.TryGetValue(@event.PlanItemDefinitionId, out var instances))
            {
                instances = new Dictionary<string, int>();
                Children[@event.PlanItemDefinitionId] = instances;
            }

            instances[@event.PlanItemInstanceId] = @event.Repetition;
        }

        public class CriterionStore
        {
            public string SatisfiedByAddress { get; private set; }
            public CriterionState State { get; private set; }

            public void Apply(CriterionSatisfied @event)
            {
                SatisfiedByAddress = $"{@event.SourceScope}.{@event.SourceId}";
                State = CriterionState.Satisfied;
                if (@event.OnPartOccurred)
                {
                    State |= CriterionState.OnPartOccurred;
                }
            }
        }
    }
}
