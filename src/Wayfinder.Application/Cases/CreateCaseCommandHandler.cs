using System;
using System.Threading;
using System.Threading.Tasks;
using Wayfinder.Application.Mediator;
using Wayfinder.Application.Results;
using Wayfinder.Contracts.V1;
using Wayfinder.Grains.Interfaces.Plan.Case;
using Orleans;
// The Create transition is the DOMAIN enum; aliased to avoid the ambiguity with Wayfinder.Contracts.V1's
// PlanItemTransition mirror (CaseView is pulled from that same namespace).
using PlanItemTransition = Wayfinder.Grains.Interfaces.Model.PlanItemTransition;

namespace Wayfinder.Application.Cases
{
    // ADO #32 - create handler, lifted from #39's CaseOperations.CreateCaseAsync. Creates a live
    // case instance keyed (newGuid, "CPM") and drives it through Create (Table 8.6 - the
    // CasePlanModel goes straight to Active). A missing definitionId, or one that was never deployed
    // (CaseGrain.Create's own InvalidOperationException guard), becomes a BadRequest CommandResult
    // instead of a thrown exception - the Unit-2 HTTP layer maps that status to 400.
    public sealed class CreateCaseCommandHandler
        : ICommandHandler<CreateCaseCommand, CommandResult<CaseView>>
    {
        private readonly IClusterClient _clusterClient;

        public CreateCaseCommandHandler(IClusterClient clusterClient)
        {
            _clusterClient = clusterClient ?? throw new ArgumentNullException(nameof(clusterClient));
        }

        public async Task<CommandResult<CaseView>> HandleAsync(
            CreateCaseCommand command, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(command.DefinitionId))
            {
                return CommandResult<CaseView>.BadRequest("definitionId is required");
            }

            var caseInstanceId = Guid.NewGuid();
            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, CaseViewProjector.CaseScope);

            try
            {
                // Throws InvalidOperationException if the definition was never deployed (CaseGrain.
                // Create's own guard) - a caller error, mapped to BadRequest rather than propagating.
                await caseGrain.Create(command.DefinitionId);
            }
            catch (InvalidOperationException ex)
            {
                return CommandResult<CaseView>.BadRequest(ex.Message);
            }

            var snapshot = await caseGrain.Trigger(PlanItemTransition.Create);
            var view = await CaseViewProjector.BuildAsync(_clusterClient, caseInstanceId, snapshot);
            return CommandResult<CaseView>.Success(view);
        }
    }
}
