using System;
using System.Collections.Generic;
using System.Linq;

namespace Flow.Grains.Plan.PlanItem.Definitions
{
    [Serializable]
    public class DefinitionGraphNode
    {
        public string Scope => $"{Parent?.Scope}{(string.IsNullOrEmpty(Parent?.Scope) ? "" : ".")}{Parent?.Id}";
        public string Address => $"{Scope}{(string.IsNullOrEmpty(Scope) ? "" : ".")}{Id}";
        public string Id { get; }

        public DefinitionGraphNode Parent { get; protected set; }
        private ICollection<DefinitionGraphNode> _children;
        public IEnumerable<DefinitionGraphNode> Children => _children?.ToArray();

        public DefinitionGraphNode(string id)
        {
            Id = id;
        }

        public DefinitionGraphNode AddNode(string id)
        {
            var node = new DefinitionGraphNode(id)
            {
                Parent = this
            };

            if (_children == null) _children = new List<DefinitionGraphNode>();
            _children.Add(node);

            return node;
        }
    }
}
