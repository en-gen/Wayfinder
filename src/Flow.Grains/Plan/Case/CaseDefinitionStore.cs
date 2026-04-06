using System.Collections.Generic;
using System.Runtime.Serialization;
using Flow.Grains.Plan.Case.Events;
using Flow.Grains.Plan.PlanItem.Definitions;

namespace Flow.Grains.Plan.Case
{
    public class CaseDefinitionStore : BaseStore
    {
        public Interfaces.Model.Case Definition { get; private set; }
        public IDictionary<string, DefinitionGraphNode> DefinitionIndex { get; private set; }

        [IgnoreDataMember]
        public bool Defined => Definition != null;

        public void Apply(CaseDefinitionDefined @event)
        {
            Created = @event.Created;
            Definition = @event.Definition;

            IDictionary<string, DefinitionGraphNode> Index(DefinitionGraphNode node, IDictionary<string, DefinitionGraphNode> index)
            {
                index[node.Address] = node;

                if (node.Children != null)
                {
                    foreach (var child in node.Children)
                    {
                        Index(child, index);
                    }
                }

                return index;
            }

            DefinitionIndex = Index(@event.DefinitionRoot, new Dictionary<string, DefinitionGraphNode>());
        }
    }
}
