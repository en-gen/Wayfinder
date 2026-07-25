using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wayfinder.Application.Mediator;
using Wayfinder.Application.Results;
using Wayfinder.Grains.Interchange;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.Case;
using Orleans;

namespace Wayfinder.Application.Cases
{
    // ADO #32 - deploy handler, lifted from #39's CaseOperations.DeployDefinitionAsync. The proven
    // front door: CmmnXmlSerializer.Import -> CmmnCapabilityLint -> ToDeployableCase ->
    // ICaseDefinitionGrain.Define. Every expected "bad input" outcome (empty body, malformed XML,
    // a zero-or-multi-case document) becomes a BadRequest CommandResult rather than a thrown
    // exception - the Unit-2 HTTP layer maps that status to 400 without catching anything.
    public sealed class DeployDefinitionCommandHandler
        : ICommandHandler<DeployDefinitionCommand, CommandResult<DeployResult>>
    {
        private readonly IClusterClient _clusterClient;

        public DeployDefinitionCommandHandler(IClusterClient clusterClient)
        {
            _clusterClient = clusterClient ?? throw new ArgumentNullException(nameof(clusterClient));
        }

        public async Task<CommandResult<DeployResult>> HandleAsync(
            DeployDefinitionCommand command, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(command.CmmnXml))
            {
                return CommandResult<DeployResult>.BadRequest("request body is empty; expected CMMN 1.1 XML");
            }

            // Import failures (malformed XML, wrong root/namespace) are expected bad input, not
            // exceptions - surface them as a typed BadRequest.
            var import = CmmnXmlSerializer.Import(command.CmmnXml);
            if (import.IsError)
            {
                return CommandResult<DeployResult>.BadRequest(import.Message);
            }

            // Honesty gate (#20): capability-lint findings are surfaced as non-fatal warnings, never
            // a hard failure - importing a model with an unsupported construct is loud but allowed.
            var lint = CmmnCapabilityLint.Lint(import.Value);
            var warnings = lint.Findings.Select(f => f.ToString()).ToArray();

            // ToDeployableCase() selects the single <case> in the document; a zero-or-multi-case
            // document is a caller error (bad input), so its InvalidOperationException becomes a
            // typed BadRequest rather than propagating.
            Case @case;
            try
            {
                @case = import.Value.ToDeployableCase();
            }
            catch (InvalidOperationException ex)
            {
                return CommandResult<DeployResult>.BadRequest(ex.Message);
            }

            // A supplied id names the deployed definition (the ICaseDefinitionGrain key); otherwise
            // a fresh, collision-free id is minted. Mirrors the flagship integration test's
            // "@case.Id = $"case-{ShortGuid.NewGuid()}"".
            @case.Id = string.IsNullOrWhiteSpace(command.CaseId)
                ? $"case-{ShortGuid.NewGuid()}"
                : command.CaseId;

            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, @case.Id)
                .Define(@case);

            // Warnings live on both the DeployResult payload (its natural home) and the Result
            // envelope (so a transport can surface them uniformly across every result type) - one
            // source, populated in both places.
            return CommandResult<DeployResult>.Success(new DeployResult(@case.Id, warnings), warnings);
        }
    }
}
