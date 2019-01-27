using System;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.CmmnElement;

namespace Flow.Grains.Plan.PlanItem
{
    [Serializable]
    public class PlanItemStore : CmmnElementStore<Interfaces.Model.PlanItem>
    {
        public PlanItemDefinition PlanItemDefinition { get; private set; }
        
        public bool Required { get; private set; }

        public PlanItemState PlanItemState { get; private set; }
        public PlanItemState? ParentSuspendState { get; private set; }

        public CriterionStore EntryCriterionStore { get; } = new CriterionStore();
        public CriterionStore ExitCriterionStore { get; } = new CriterionStore();

        public bool Repeated { get; private set; }
        
        public void Apply(Defined @event)
        {
            base.Apply(@event);
            PlanItemDefinition = @event.PlanItemDefinition;
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

        public void Apply(ManualActivationRuleEvaluated @event)
        {
            Updated = @event.Updated;
        }

        public void Apply(RepetitionRuleEvaluated @event)
        {
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

        public void Apply(ParentTerminated @event)
        {
            Updated = @event.Updated;
        }
    }

    [Serializable]
    public class Defined : CmmnElementDefined<Interfaces.Model.PlanItem>
    {
        public PlanItemDefinition PlanItemDefinition { get; set; }
    }

    [Serializable]
    public class Transitioned
    {
        public DateTime Updated { get; } = DateTime.UtcNow;

        public PlanItemState Source { get; set; }
        public PlanItemState Destination { get; set; }
        public PlanItemTransition Trigger { get; set; }
    }

    [Serializable]
    public class EntryCriterionSatisfied
    {
        public DateTime Updated { get; set; } = DateTime.UtcNow;

        public string SourceScope { get; set; }
        public string SourceId { get; set; }
        public bool OnPartOccurred { get; set; }
    }

    [Serializable]
    public class ExitCriterionSatisfied
    {
        public DateTime Updated { get; set; } = DateTime.UtcNow;

        public string SourceScope { get; set; }
        public string SourceId { get; set; }
        public bool OnPartOccurred { get; set; }
    }

    [Serializable]
    public class RequiredRuleEvaluated
    {
        public DateTime Updated { get; set; } = DateTime.UtcNow;

        public bool Result { get; set; }
    }

    [Serializable]
    public class ManualActivationRuleEvaluated
    {
        public DateTime Updated { get; set; } = DateTime.UtcNow;

        public bool Result { get; set; }
    }

    [Serializable]
    public class RepetitionRuleEvaluated
    {
        public DateTime Updated { get; set; } = DateTime.UtcNow;

        public bool Result { get; set; }
    }

    [Serializable]
    public class ParentSuspended
    {
        public DateTime Updated { get; set; } = DateTime.UtcNow;
    }

    [Serializable]
    public class ParentResumed
    {
        public DateTime Updated { get; set; } = DateTime.UtcNow;
    }

    [Serializable]
    public class ParentTerminated
    {
        public DateTime Updated { get; set; } = DateTime.UtcNow;
    }
}
