using System;
using System.Collections.Generic;

namespace Flow.Contracts.V1
{
    // ADO #32/#33 - the POST /api/v1/definitions response body: the deployed definition's id plus
    // any non-fatal capability-lint warnings (#20's honesty gate). A thin wire projection of
    // Flow.Application.Cases.DeployResult - kept separate (rather than serializing DeployResult
    // itself across the wire) for the same reason CaseView exists alongside the domain snapshot: V1
    // DTOs are versioned by namespace here, independent of the Application layer's own shapes.
    public sealed class DeployDefinitionResponse
    {
        public string DefinitionId { get; init; }
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }
}
