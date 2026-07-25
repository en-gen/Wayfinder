using System;
using Wayfinder.Grains.Interfaces;

namespace Wayfinder.Grains.Events
{
    // ADO #59 - implemented by every journaled lifecycle event so the RaiseEvent-path stamping in
    // Events.ActorStamping (called from CmmnElementGrain.RaiseEvent and CaseDefinitionGrain.Define)
    // can set the acting identity uniformly, without per-event-type stamping code at each RaiseEvent
    // call site.
    //
    // Implemented directly on each event type (BaseCreated, BaseUpdate, and - since they predate
    // this work item and don't derive from either - CaseFileItem's and Sentry's own event types)
    // rather than funnelled through one new shared abstract base class: this codebase's journaled
    // events already split across two type hierarchies with no common ancestor below `object`
    // (CmmnElementGrain<,>'s events - BaseCreated/BaseUpdate and everything deriving from them -
    // and CaseDefinitionGrain's standalone CaseDefinitionDefined : BaseCreated), plus CaseFileItem/
    // Sentry's ChildAdded/Discarded/ValueChanged/Faulted/Satisfied/etc., which never adopted
    // BaseUpdate and instead duplicate an inline `Updated` field. Inserting a brand-new base class
    // under any of these EXISTING, already-persistable types would change its wire shape (Orleans's
    // [GenerateSerializer] codegen treats "no base class" and "has a [GenerateSerializer] base
    // class" as different serialization structures, not just an additive field list) - safe for a
    // type that has never shipped, but exactly the kind of retroactive hierarchy change this work
    // item exists to avoid for events that may already have persisted history. Implementing this
    // interface, by contrast, adds no state and does not change any existing type's serialization
    // shape at all - only the new [Id(n)] fields on each implementing type do, which is the
    // documented-safe, purely-additive case ("new [Id(n)] fields default harmlessly on replay of
    // old events").
    public interface IActorStampedEvent
    {
        Guid ActorPrincipalId { get; set; }
        ActorPrincipalType ActorPrincipalType { get; set; }
        string ActorOnBehalfOf { get; set; }
    }
}
