using System;
using System.Collections.Generic;
using System.Linq;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Definitions
{
    [GenerateSerializer]
    public class DefinitionGraphNode
    {
        public string Scope => $"{Parent?.Scope}{(string.IsNullOrEmpty(Parent?.Scope) ? "" : ".")}{Parent?.Id}";
        public string Address => $"{Scope}{(string.IsNullOrEmpty(Scope) ? "" : ".")}{Id}";
        [Id(0)]
        public string Id { get; }

        [Id(1)]
        public DefinitionGraphNode Parent { get; protected set; }
        [Id(2)]
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
