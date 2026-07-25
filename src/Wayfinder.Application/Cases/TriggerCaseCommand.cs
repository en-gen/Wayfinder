using System;
using Wayfinder.Application.Mediator;
using Wayfinder.Application.Results;
using Wayfinder.Contracts.V1;
// Both Wayfinder.Contracts.V1 and Wayfinder.Grains.Interfaces.Model declare a PlanItemTransition; this command
// carries the DOMAIN one (see the type remarks), so it is aliased explicitly to avoid the ambiguity
// with the V1 wire mirror that CaseView is pulled from.
using PlanItemTransition = Wayfinder.Grains.Interfaces.Model.PlanItemTransition;

namespace Wayfinder.Application.Cases
{
    // ADO #32 - drives an explicit PlanItemTransition against the case root (CasePlanModel) and
    // returns the resulting CaseView. An invalid transition (e.g. triggering an uninitialized or
    // Closed case) comes back as a BadRequest CommandResult (see TriggerCaseCommandHandler), never a
    // thrown exception. Lifted from #39's ICaseOperations.TriggerCaseAsync.
    //
    // Transition is the DOMAIN enum (Wayfinder.Grains.Interfaces.Model.PlanItemTransition), not the wire
    // Wayfinder.Contracts.V1 mirror: this command is an application-layer instruction handed straight to
    // the grain, so it speaks the domain's vocabulary. The future Unit-2 HTTP layer maps a
    // TriggerCaseRequest's V1 transition to this domain enum when it builds the command - keeping
    // that translation at the transport edge, out of the handler.
    public sealed record TriggerCaseCommand(Guid CaseId, PlanItemTransition Transition)
        : ICommand<CommandResult<CaseView>>;
}
