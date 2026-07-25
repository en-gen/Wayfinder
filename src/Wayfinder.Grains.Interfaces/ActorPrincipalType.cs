namespace Flow.Grains.Interfaces
{
    // ADO #59 - who/what CaseRequestContext.ActorPrincipalId actually names. The overwhelming
    // majority of callers today are User (IdentityContextMiddleware resolves every authenticated
    // request's "sub" claim to a human's UserId - see its remarks). Client exists for the
    // documented-but-not-yet-implemented S2S path (a per-tenant service principal calling on its
    // own authority, optionally asserting an end-user identity via CaseRequestContext.
    // ActorOnBehalfOf rather than impersonating CaseRequestContext.UserId directly - see that
    // property's remarks). No [GenerateSerializer] needed: Orleans's serializer has native,
    // codegen-free support for enums (see PlanItemState, used identically across the journaled
    // event types this enum now rides alongside - e.g. Transitioned.Source/Destination).
    public enum ActorPrincipalType
    {
        User,
        Client
    }
}
