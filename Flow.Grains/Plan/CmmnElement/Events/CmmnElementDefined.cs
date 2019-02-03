using System;

namespace Flow.Grains.Plan.CmmnElement.Events
{
    [Serializable]
    public class CmmnElementDefined<TDefinition> : BaseCreated
        where TDefinition : Interfaces.Model.CmmnElement
    {
        public Guid? CaseDefinitionId { get; set; }

        public TDefinition Definition { get; set; }
    }
}