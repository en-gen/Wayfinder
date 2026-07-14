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
    // ADO #32 - get handler, lifted from #39's CaseOperations.GetCaseAsync. Reads the case root's
    // snapshot (keyed (caseId, "CPM")). An Orleans grain always "exists" as a virtual actor, so a
    // case that was never Create()'d still answers GetSnapshot - but with a null Definition. That
    // null IS the not-found signal, surfaced here as a NotFound QueryResult (the Unit-2 HTTP layer
    // maps that status to 404).
    public sealed class GetCaseQueryHandler
        : IQueryHandler<GetCaseQuery, QueryResult<CaseView>>
    {
        private readonly IClusterClient _clusterClient;

        public GetCaseQueryHandler(IClusterClient clusterClient)
        {
            _clusterClient = clusterClient ?? throw new ArgumentNullException(nameof(clusterClient));
        }

        public async Task<QueryResult<CaseView>> HandleAsync(
            GetCaseQuery query, CancellationToken cancellationToken)
        {
            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(query.CaseId, CaseViewProjector.CaseScope);
            var snapshot = await caseGrain.GetSnapshot();

            if (snapshot?.Definition == null)
            {
                return QueryResult<CaseView>.NotFound();
            }

            var view = await CaseViewProjector.BuildAsync(_clusterClient, query.CaseId, snapshot);
            return QueryResult<CaseView>.Success(view);
        }
    }
}
