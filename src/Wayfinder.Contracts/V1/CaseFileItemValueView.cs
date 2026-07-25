using System.Text.Json.Nodes;

namespace Flow.Contracts.V1
{
    // ADO #58 - GetCaseFileItemValueAtQuery's result: an as-of read of a case-file item's Value.
    // JsonNode rides straight through unwrapped (no anti-corruption mapping needed - a
    // CaseFileItem's content IS arbitrary JSON by design, see CaseFileItemStore.Value's remarks,
    // and System.Text.Json.Nodes is inbox in the shared framework, not an extra dependency for
    // Flow.Contracts' "no ProjectReferences" rule - see Flow.Contracts.csproj's remarks).
    public sealed class CaseFileItemValueView
    {
        public int Version { get; init; }
        public JsonNode Value { get; init; }
    }
}
