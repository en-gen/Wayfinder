using System;
using Flow.Grains.Interfaces.Model;

namespace Flow.Grains.Events
{
    [Serializable]
    public class PlanItemTransitionedEvent : BaseEvent
    {
        public PlanItemTransition StandardEvent { get; }

        public PlanItemState Source { get; }
        public PlanItemState Destination { get; }

        public string ExitCriterionRef { get; }

        public PlanItemTransitionedEvent(
            string planItemScope,
            string planItemId,
            PlanItemTransition standardEvent,
            PlanItemState source,
            PlanItemState destination,
            string exitCriterionRef = null) :
            base(planItemScope, planItemId)
        {
            StandardEvent = standardEvent;
            Source = source;
            Destination = destination;
            ExitCriterionRef = exitCriterionRef;
        }
    }
}
