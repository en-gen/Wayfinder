using System;
using Flow.Grains.Plan.CmmnElement.Events;

namespace Flow.Grains.Plan.PlanItem.Events
{
    [Serializable]
    public class ChildCreated : BaseUpdate
    {
        public string PlanItemDefinitionId { get; set; }
        public string PlanItemInstanceId { get; set; }
        public int Repetition { get; set; }
    }
}
