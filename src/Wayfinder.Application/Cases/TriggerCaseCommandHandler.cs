using System;
using System.Threading;
using System.Threading.Tasks;
using Flow.Application.Mediator;
using Flow.Application.Results;
using Flow.Contracts.V1;
using Flow.Grains.Interfaces.Plan.Case;
using Orleans;

namespace Flow.Application.Cases
{
    // ADO #32 - trigger handler, lifted from #39's CaseOperations.TriggerCaseAsync. Drives an
    // explicit PlanItemTransition against the case root (keyed (caseId, "CPM")) and projects the
    // resulting snapshot. An invalid transition (uninitialized or Closed case) surfaces as
    // CaseGrain.Trigger's InvalidOperationException, mapped here to a BadRequest CommandResult
    // instead of a thrown exception - the Unit-2 HTTP layer maps that status to 400.
    public sealed class TriggerCaseCommandHandler
        : ICommandHandler<TriggerCaseCommand, CommandResult<CaseView>>
    {
        private readonly IClusterClient _clusterClient;

        public TriggerCaseCommandHandler(IClusterClient clusterClient)
        {
            _clusterClient = clusterClient ?? throw new ArgumentNullException(nameof(clusterClient));
        }

        public async Task<CommandResult<CaseView>> HandleAsync(
            TriggerCaseCommand command, CancellationToken cancellationToken)
        {
            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(command.CaseId, CaseViewProjector.CaseScope);

            CaseSnapshot snapshot;
            try
            {
                snapshot = await caseGrain.Trigger(command.Transition);
            }
            catch (InvalidOperationException ex)
            {
                return CommandResult<CaseView>.BadRequest(ex.Message);
            }

            var view = await CaseViewProjector.BuildAsync(_clusterClient, command.CaseId, snapshot);
            return CommandResult<CaseView>.Success(view);
        }
    }
}
