using System;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Flow.Grains.Plan.PlanItem.Events
{
    [GenerateSerializer]
    public class Defined : CmmnElementDefined<Interfaces.Model.PlanItem>
    {
        [Id(0)]
        public PlanItemDefinition PlanItemDefinition { get; set; }
        [Id(1)]
        public int Repetition { get; set; }
    }
}