using System.Collections.Generic;
using System.Xml;

namespace Flow.Grains.Interfaces.Model
{
    public partial class PlanItem
    {
        public PlanItem(PlanItem src)
        {
            // PlanItem properties
            DefinitionRef = src.DefinitionRef;
            _entryCriteria = new List<EntryCriterion>(src.EntryCriteria);
            _exitCriteria = new List<ExitCriterion>(src.ExitCriteria);
            ItemControl = src.ItemControl;
            Name = src.Name;

            // CmmnElement (base) properties
            _anyAttributes = new List<XmlAttribute>(src.AnyAttributes);
            _documentation = new List<Documentation>(src.Documentation);
            ExtensionElements = src.ExtensionElements;
            Id = src.Id;
        }
    }
}
