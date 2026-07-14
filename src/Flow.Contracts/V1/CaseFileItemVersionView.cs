using System;

namespace Flow.Contracts.V1
{
    // ADO #58 - one entry of GetCaseFileItemHistoryQuery's result: the wire projection of
    // Flow.Grains.Interfaces.Plan.CaseFileItem.CaseFileItemVersionDescriptor. See that type's
    // remarks for exactly which journaled events produce one of these.
    public sealed class CaseFileItemVersionView
    {
        public int Version { get; init; }
        public DateTime UpdatedUtc { get; init; }
        public Guid ActorPrincipalId { get; init; }
        public ActorPrincipalType ActorPrincipalType { get; init; }
        public string ActorOnBehalfOf { get; init; }

        // Null for a version whose underlying event predates ADO #58 - see
        // CaseFileItemVersionDescriptor.Transition's remarks; deliberately not guessed.
        public CaseFileItemTransition? Transition { get; init; }
    }
}
