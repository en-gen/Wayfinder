using System;
using Flow.Grains.Plan.CmmnElement.Events;
using Flow.Grains.Plan.PlanItem.Definitions;

namespace Flow.Grains.Plan.Case.Events
{
    [Serializable]
    public class CaseDefinitionDefined : BaseCreated
    {
        public Interfaces.Model.Case Definition { get; set; }
        public DefinitionGraphNode DefinitionRoot { get; set; }
    }
}
