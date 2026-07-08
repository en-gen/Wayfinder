using System;
using Flow.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Flow.Grains.Plan.PlanItem.Events
{
    [GenerateSerializer]
    public class ChildCreated : BaseUpdate
    {
        [Id(0)]
        public string PlanItemDefinitionId { get; set; }
        [Id(1)]
        public string PlanItemInstanceId { get; set; }
        [Id(2)]
        public int Repetition { get; set; }
    }
}
