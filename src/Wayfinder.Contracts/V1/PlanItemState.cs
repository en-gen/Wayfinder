namespace Wayfinder.Contracts.V1
{
    // ADO #32 - the wire projection of Wayfinder.Grains.Interfaces.Model.PlanItemState (CMMN 1.1 §8.4.1's
    // ten plan-item states). A deliberate 1:1 mirror, not a reference to the domain enum: Wayfinder.
    // Contracts takes no dependency on Wayfinder.Grains/Wayfinder.Grains.Interfaces (see Wayfinder.Contracts.csproj's
    // remarks), so Wayfinder.Application's handlers map the domain enum to this one explicitly (see
    // Wayfinder.Application/Cases/CaseViewProjector.cs) rather than this assembly referencing it directly.
    public enum PlanItemState
    {
        Uninitialized,
        Available,
        Enabled,
        Disabled,
        Active,
        Suspended,
        Failed,
        Completed,
        Closed,
        Terminated
    }
}
