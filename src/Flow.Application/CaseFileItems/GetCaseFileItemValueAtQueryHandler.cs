using System;
using System.Threading;
using System.Threading.Tasks;
using Flow.Application.Mediator;
using Flow.Application.Results;
using Flow.Contracts.V1;
using Flow.Grains.Infrastructure.Extensions;
using Orleans;

namespace Flow.Application.CaseFileItems
{
    // ADO #58 - as-of value handler. Same authorization/existence checks as
    // GetCaseFileItemHistoryQueryHandler; a version outside the grain's own journal range
    // (ICaseFileItemGrain.GetValueAt's ArgumentOutOfRangeException) is caller error, mapped to a
    // BadRequest result rather than propagating - matching CreateCaseCommandHandler's own
    // "caller error -> BadRequest, not a thrown exception" convention.
    public sealed class GetCaseFileItemValueAtQueryHandler
        : IQueryHandler<GetCaseFileItemValueAtQuery, QueryResult<CaseFileItemValueView>>
    {
        private readonly IClusterClient _clusterClient;

        public GetCaseFileItemValueAtQueryHandler(IClusterClient clusterClient)
        {
            _clusterClient = clusterClient ?? throw new ArgumentNullException(nameof(clusterClient));
        }

        public async Task<QueryResult<CaseFileItemValueView>> HandleAsync(
            GetCaseFileItemValueAtQuery query, CancellationToken cancellationToken)
        {
            if (!await CaseFileItemAccess.OwningCaseExistsAsync(_clusterClient, query.CaseId))
            {
                return QueryResult<CaseFileItemValueView>.NotFound();
            }

            var itemGrain = _clusterClient.GetCaseFileItem(query.CaseId, query.CaseFileItemId);

            if (!await itemGrain.Defined())
            {
                return QueryResult<CaseFileItemValueView>.NotFound();
            }

            System.Text.Json.Nodes.JsonNode value;
            try
            {
                value = await itemGrain.GetValueAt(query.Version);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return QueryResult<CaseFileItemValueView>.BadRequest(ex.Message);
            }

            return QueryResult<CaseFileItemValueView>.Success(new CaseFileItemValueView
            {
                Version = query.Version,
                Value = value
            });
        }
    }
}
