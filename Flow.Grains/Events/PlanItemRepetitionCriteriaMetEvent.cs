using System;

namespace Flow.Grains.Events
{
    [Serializable]
    public class PlanItemRepetitionCriteriaMetEvent : BaseEvent
    {
        public string PlanItemInstanceId { get; }
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
