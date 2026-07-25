using System;
using System.Collections.Generic;
using Wayfinder.Application.Mediator;
using Wayfinder.Application.Results;
using Wayfinder.Contracts.V1;

namespace Wayfinder.Application.CaseFileItems
{
    // ADO #58 - reads a case-file item's ordered version history. CaseId + CaseFileItemId
    // together address the underlying ICaseFileItemGrain (the same (caseInstanceId, "casefile.
    // <id>") compound key CaseFileItemAddress/GrainFactoryExtensions.GetCaseFileItem already use)
    // - CaseId doubles as the tenant-isolation anchor (see CaseFileItemAccess), since the
    // CaseFileItem grain itself carries no TenantId of its own.
    public sealed record GetCaseFileItemHistoryQuery(Guid CaseId, string CaseFileItemId)
        : IQuery<QueryResult<IReadOnlyList<CaseFileItemVersionView>>>;
}
