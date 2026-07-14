namespace Flow.Contracts.V1
{
    // ADO #58 - the wire projection of Flow.Grains.Interfaces.ActorPrincipalType. A deliberate 1:1
    // mirror kept in lockstep with that enum's members, same reasoning as PlanItemState.cs's
    // remarks: Flow.Contracts takes no dependency on Flow.Grains/Flow.Grains.Interfaces, so
    // Flow.Application's handlers map the domain enum to this one explicitly (see
    // Flow.Application/CaseFileItems/CaseFileItemHistoryProjector.cs).
    public enum ActorPrincipalType
    {
        User,
        Client
    }
}
