using Wayfinder.Application.Mediator;
using Wayfinder.Application.Results;
using Wayfinder.Contracts.V1;

namespace Wayfinder.Application.Cases
{
    // ADO #32 - creates a new case instance from a previously deployed definition and drives it
    // through the Create transition (Table 8.6 - the CasePlanModel goes straight to Active),
    // returning the projected CaseView. A missing/never-deployed definition comes back as a
    // BadRequest CommandResult (see CreateCaseCommandHandler), never a thrown exception. Lifted from
    // #39's ICaseOperations.CreateCaseAsync.
    public sealed record CreateCaseCommand(string DefinitionId)
        : ICommand<CommandResult<CaseView>>;
}
