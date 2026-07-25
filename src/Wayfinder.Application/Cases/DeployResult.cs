using System;
using System.Collections.Generic;

namespace Wayfinder.Application.Cases
{
    // ADO #32 - the value DeployDefinitionCommandHandler returns on success: the deployed
    // definition's id plus any non-fatal capability-lint warnings (#20's honesty gate). Lives in
    // Wayfinder.Application (not Wayfinder.Contracts) - it is not part of the M2 V1 wire-DTO set; a future
    // Unit-2 HTTP layer shapes its own response from CommandResult<DeployResult> the same way it
    // will shape responses from the other CommandResult<T>/QueryResult<T> handlers return.
    public sealed class DeployResult
    {
        public DeployResult(string definitionId, IReadOnlyList<string> warnings)
        {
            DefinitionId = definitionId ?? throw new ArgumentNullException(nameof(definitionId));
            Warnings = warnings ?? Array.Empty<string>();
        }

        public string DefinitionId { get; }
        public IReadOnlyList<string> Warnings { get; }
    }
}
