using System;
using Flow.Application.Mediator;
using Flow.Application.Results;
using Flow.Contracts.V1;

namespace Flow.Application.Cases
{
    // ADO #32 - reads a case instance's current state as a CaseView. A case that was never
    // Create()'d comes back as a NotFound QueryResult (an Orleans grain always "exists" as a virtual
    // actor, so a null Definition on its snapshot IS the not-found signal; see GetCaseQueryHandler).
    // Lifted from #39's ICaseOperations.GetCaseAsync.
    public sealed record GetCaseQuery(Guid CaseId)
        : IQuery<QueryResult<CaseView>>;
}
