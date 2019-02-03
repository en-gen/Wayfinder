using System;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.CmmnElement.Events;

namespace Flow.Grains.Plan.PlanItem.Events
{
    [Serializable]
    public class Transitioned : BaseUpdate
    {
        public PlanItemState Source { get; set; }
        public PlanItemState Destination { get; set; }
        public PlanItemTransition Trigger { get; set; }
    }
}