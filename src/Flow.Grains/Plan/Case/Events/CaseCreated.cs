using System;
using Flow.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Flow.Grains.Plan.Case.Events
{
    [GenerateSerializer]
    public class CaseCreated : CmmnElementDefined<Interfaces.Model.Case>
    {
        [Id(0)]
        public int Repetition { get; set; }
    }
}
