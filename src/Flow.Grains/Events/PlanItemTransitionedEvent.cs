using System;
using Flow.Grains.Interfaces.Model;

namespace Flow.Grains.Events
{
    [Serializable]
    public class PlanItemTransitionedEvent : BaseEvent
    {
        public string SourceInstanceId { get; }
        public PlanItemTransition StandardEvent { get; }

        public PlanItemState Source { get; }
        public PlanItemState Destination { get; }

        public string ExitCriterionRef { get; }

        public PlanItemTransitionedEvent(
            string planItemScope,
            string planItemInstanceId,
            string planItemDefinitionId,
            PlanItemTransition standardEvent,
            PlanItemState source,
            PlanItemState destination,
            string exitCriterionRef = null) :
            base(planItemScope, planItemDefinitionId)
        {
            SourceInstanceId = planItemInstanceId;
            StandardEvent = standardEvent;
            Source = source;
            Destination = destination;
            ExitCriterionRef = exitCriterionRef;
        }
    }
}
