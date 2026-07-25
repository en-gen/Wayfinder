namespace Flow.Contracts.V1
{
    // ADO #32 - the wire projection of Flow.Grains.Interfaces.Model.PlanItemState (CMMN 1.1 §8.4.1's
    // ten plan-item states). A deliberate 1:1 mirror, not a reference to the domain enum: Flow.
    // Contracts takes no dependency on Flow.Grains/Flow.Grains.Interfaces (see Flow.Contracts.csproj's
    // remarks), so Flow.Application's handlers map the domain enum to this one explicitly (see
    // Flow.Application/Cases/CaseViewProjector.cs) rather than this assembly referencing it directly.
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
