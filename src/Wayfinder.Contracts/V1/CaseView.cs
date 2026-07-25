using System;
using System.Collections.Generic;

namespace Wayfinder.Contracts.V1
{
    // ADO #32 - a lightweight, flattened projection of a case instance's current state: the
    // case-level PlanItemState plus every plan item instantiated so far anywhere under the
    // CasePlanModel (walked recursively through nested stages by Wayfinder.Application's
    // CaseViewProjector). Not nested to mirror the definition's stage tree - flat is simpler for a
    // thin slice, and every PlanItemView already carries enough identity (Id/Name/Type) to be
    // useful on its own. Mirrors #39's Wayfinder.Silo.Api.CaseView; returned by CreateCaseCommand,
    // TriggerCaseCommand, and GetCaseQuery's handlers.
    public sealed class CaseView
    {
        public Guid CaseId { get; init; }
        public string DefinitionId { get; init; }
        public PlanItemState State { get; init; }
        public IReadOnlyList<PlanItemView> PlanItems { get; init; } = Array.Empty<PlanItemView>();
    }
}
