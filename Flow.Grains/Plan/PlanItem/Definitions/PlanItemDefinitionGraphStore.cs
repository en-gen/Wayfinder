using System;
using System.Collections.Generic;

namespace Flow.Grains.Plan.PlanItem.Definitions
{
    [Serializable]
    public class PlanItemDefinitionGraphStore
    {
        public IDictionary<string, Node> Index { get; } = new Dictionary<string, Node>();

        public void IndexNode(Node node) => Index[node.Address] = node;
    }

    [Serializable]
    public class Node
    {
        public string Address => $"{Scope}{(string.IsNullOrEmpty(Scope) ? "" : ".")}{Id}";
        public string Scope => $"{Parent?.Scope}{(string.IsNullOrEmpty(Parent?.Scope) ? "" : ".")}{Parent?.Id}";
        public string Id { get; set; }

        public Node Parent { get; protected set; }

        public Node AddNode(string id)
        {
            var node = new Node
            {
                Id = id,
                Parent = this
            };
            return node;
        }
    }
}
