using System;
using Flow.Grains.Interfaces;

namespace Flow.Grains.Events
{
    // ADO #59 - the one place a journaled event's acting identity gets set, from
    // CaseRequestContext (Orleans RequestContext/AsyncLocal - populated from the authenticated
    // caller by Flow.Api's IdentityContextMiddleware). Called from exactly two append points -
    // CmmnElementGrain<,>.RaiseEvent (covers Case/PlanItem/CaseFileItem/Sentry/PlanningTable/Role -
    // every grain in that hierarchy, automatically, via a single shadowed method) and
    // CaseDefinitionGrain.Define (the one other JournaledGrain root in this codebase, which does
    // not derive from CmmnElementGrain) - so "central" here means "one piece of logic, two call
    // sites dictated by this codebase's two distinct JournaledGrain hierarchies", not stamping
    // scattered across individual event constructions.
    //
    // A no-op for any event that does not implement IActorStampedEvent (there are none left after
    // this work item's sweep of every RaiseEvent'd type, but this keeps the helper safe to call
    // unconditionally rather than requiring every call site to type-check first).
    //
    // TODO #56/#57: outbound CloudEvents envelopes for these journaled events don't exist yet (no
    // sink has been built) - when they are, the envelope's attribution should read from the
    // already-stamped IActorStampedEvent fields on the event being published, not re-derive the
    // actor from CaseRequestContext at publish time (the publish-time caller may not be the same
    // request that originally raised the event).
    internal static class ActorStamping
    {
        public static void Apply<TEvent>(TEvent @event)
        {
            if (@event is IActorStampedEvent stamped)
            {
                stamped.ActorPrincipalId = TryGetUserId();
                stamped.ActorPrincipalType = CaseRequestContext.ActorPrincipalType;
                stamped.ActorOnBehalfOf = CaseRequestContext.ActorOnBehalfOf;
            }
        }

        // CaseRequestContext.UserId throws when unset (ADO #33 - enforced deliberately for the
        // request-facing surface, where an authenticated caller is mandatory). RaiseEvent, unlike
        // that surface, is also reached by flows with no ambient identity at all and no request to
        // trace back to - a timer tick (TimerTickJob publishes to a stream with no RequestContext
        // to propagate; TimerEventListenerBehavior's handler then calls Host.RaiseEvent from that
        // stream callback) or a PlanItem/Sentry transition cascade re-entering a previously-idle,
        // just-reactivated grain. Those flows already worked before this work item and must keep
        // working - RaiseEvent must never start faulting a case's normal operation just because
        // nobody happened to be "logged in" for that particular internal step - so the actor
        // defaults to Guid.Empty there (indistinguishable, by design, from a replayed pre-#59
        // event that never had an actor at all) rather than propagating UserId's exception.
        private static Guid TryGetUserId()
        {
            try
            {
                return CaseRequestContext.UserId;
            }
            catch (InvalidOperationException)
            {
                return Guid.Empty;
            }
        }
    }
}
