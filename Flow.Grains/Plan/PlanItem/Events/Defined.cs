using System;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.CmmnElement.Events;

namespace Flow.Grains.Plan.PlanItem.Events
{
    [Serializable]
    public class Defined : CmmnElementDefined<Interfaces.Model.PlanItem>
    {
        public PlanItemDefinition PlanItemDefinition { get; set; }
        public int Repetition { get; set; }
    }
}