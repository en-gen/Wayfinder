using System;
using Flow.Application.Mediator;
using Flow.Application.Results;
using Flow.Contracts.V1;

namespace Flow.Application.CaseFileItems
{
    // ADO #58 - as-of read of a case-file item's Value at a specific journal version. See
    // GetCaseFileItemHistoryQuery's remarks for CaseId/CaseFileItemId; Version is the 1-based
    // journal sequence number ICaseFileItemGrain.GetHistory/GetValueAt use.
    public sealed record GetCaseFileItemValueAtQuery(Guid CaseId, string CaseFileItemId, int Version)
        : IQuery<QueryResult<CaseFileItemValueView>>;
}
