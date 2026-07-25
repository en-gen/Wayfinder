using Wayfinder.Application.Mediator;
using Wayfinder.Application.Results;

namespace Wayfinder.Application.Cases
{
    // ADO #32 - imports + deploys a .cmmn XML definition. CaseId, if supplied, becomes the deployed
    // definition's id (ICaseDefinitionGrain's compound key); otherwise a fresh "case-{ShortGuid}"
    // id is minted. Import/lint/single-case failures come back as a BadRequest CommandResult, never
    // a thrown exception (see DeployDefinitionCommandHandler). Lifted from #39's
    // ICaseOperations.DeployDefinitionAsync.
    public sealed record DeployDefinitionCommand(string CmmnXml, string CaseId = null)
        : ICommand<CommandResult<DeployResult>>;
}
