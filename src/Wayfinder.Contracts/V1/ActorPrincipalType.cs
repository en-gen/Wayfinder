namespace Wayfinder.Contracts.V1
{
    // ADO #58 - the wire projection of Wayfinder.Grains.Interfaces.ActorPrincipalType. A deliberate 1:1
    // mirror kept in lockstep with that enum's members, same reasoning as PlanItemState.cs's
    // remarks: Wayfinder.Contracts takes no dependency on Wayfinder.Grains/Wayfinder.Grains.Interfaces, so
    // Wayfinder.Application's handlers map the domain enum to this one explicitly (see
    // Wayfinder.Application/CaseFileItems/CaseFileItemHistoryProjector.cs).
    public enum ActorPrincipalType
    {
        User,
        Client
    }
}
