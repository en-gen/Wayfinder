using System;
using Orleans;

namespace Flow.Grains.Events
{
    [GenerateSerializer]
    public class PlanItemRepetitionCriteriaMetEvent : BaseEvent
    {
        [Id(0)]
        public string PlanItemInstanceId { get; }
        [Id(1)]
        public int CurrentRepetition { get; }

        public PlanItemRepetitionCriteriaMetEvent(
            string planItemScope,
            string planItemInstanceId,
            string planItemDefinitionId,
            int currentRepetition) :
            base(planItemScope, planItemDefinitionId)
        {
            PlanItemInstanceId = planItemInstanceId;
            CurrentRepetition = currentRepetition;
        }
    }
}
