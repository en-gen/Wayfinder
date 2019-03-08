using System;
using Flow.Grains.Plan.CmmnElement.Events;

namespace Flow.Grains.Plan.Case.Events
{
    [Serializable]
    public class CaseCreated : CmmnElementDefined<Interfaces.Model.Case>
    {
        public int Repetition { get; set; }
    }
}
