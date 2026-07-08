using System;
using Flow.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Flow.Grains.Plan.PlanItem.Events
{
    [GenerateSerializer]
    public class TimerStartTriggerOccurred : BaseUpdate
    {
        [Id(0)]
        public DateTime Occurred { get; set; }
    }
}
