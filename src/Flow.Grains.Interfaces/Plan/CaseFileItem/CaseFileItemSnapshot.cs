using System.Text.Json.Nodes;
using Orleans;

namespace Flow.Grains.Interfaces.Plan.CaseFileItem
{
    [GenerateSerializer]
    public class CaseFileItemSnapshot
    {
        [Id(0)]
        public Model.CaseFileItem Definition { get; set; }

        [Id(1)]
        public CaseFileItemState CaseFileItemState { get; set; }

        [Id(2)]
        public JsonNode Value { get; set; }
    }
}
