using System;
using Flow.Grains.Interfaces.Model;
using Orleans;

namespace Flow.Grains.Events
{
    [GenerateSerializer]
    public class PlanItemTransitionedEvent : BaseEvent
    {
        [Id(0)]
        public string SourceInstanceId { get; }
        [Id(1)]
        public PlanItemTransition StandardEvent { get; }

        [Id(2)]
        public PlanItemState Source { get; }
        [Id(3)]
        public PlanItemState Destination { get; }

        [Id(4)]
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
