using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wayfinder.Application.Mediator;
using Wayfinder.Application.Results;
using Wayfinder.Contracts.V1;
using Wayfinder.Grains.Infrastructure.Extensions;
using Orleans;

namespace Wayfinder.Application.CaseFileItems
{
    // ADO #58 - history handler. Authorizes via the owning case first (CaseFileItemAccess - a
    // never-created case, or one owned by a different tenant, is NotFound/throws exactly like
    // GetCaseQueryHandler's own case-level read), then checks the item itself was actually
    // Create()'d (an Orleans grain always "exists" as a virtual actor - Defined() is the
    // not-found signal here, the same role State.Definition == null plays for GetCaseQueryHandler).
    public sealed class GetCaseFileItemHistoryQueryHandler
        : IQueryHandler<GetCaseFileItemHistoryQuery, QueryResult<IReadOnlyList<CaseFileItemVersionView>>>
    {
        private readonly IClusterClient _clusterClient;

        public GetCaseFileItemHistoryQueryHandler(IClusterClient clusterClient)
        {
            _clusterClient = clusterClient ?? throw new System.ArgumentNullException(nameof(clusterClient));
        }

        public async Task<QueryResult<IReadOnlyList<CaseFileItemVersionView>>> HandleAsync(
            GetCaseFileItemHistoryQuery query, CancellationToken cancellationToken)
        {
            if (!await CaseFileItemAccess.OwningCaseExistsAsync(_clusterClient, query.CaseId))
            {
                return QueryResult<IReadOnlyList<CaseFileItemVersionView>>.NotFound();
            }

            var itemGrain = _clusterClient.GetCaseFileItem(query.CaseId, query.CaseFileItemId);

            if (!await itemGrain.Defined())
            {
                return QueryResult<IReadOnlyList<CaseFileItemVersionView>>.NotFound();
            }

            var history = await itemGrain.GetHistory();

            IReadOnlyList<CaseFileItemVersionView> views =
                history.Select(CaseFileItemHistoryProjector.ToView).ToArray();

            return QueryResult<IReadOnlyList<CaseFileItemVersionView>>.Success(views);
        }
    }
}
