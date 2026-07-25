using System;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    [GenerateSerializer]
    public class TimerStartTriggerOccurred : BaseUpdate
    {
        [Id(0)]
        public DateTime Occurred { get; set; }
    }
}
