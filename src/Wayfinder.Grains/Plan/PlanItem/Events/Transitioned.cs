using System;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    [GenerateSerializer]
    public class Transitioned : BaseUpdate
    {
        [Id(0)]
        public PlanItemState Source { get; set; }
        [Id(1)]
        public PlanItemState Destination { get; set; }
        [Id(2)]
        public PlanItemTransition Trigger { get; set; }
    }
}
