namespace Flow.Grains.Interfaces.Plan.CaseFileItem
{
    // 8.3 - CaseFileItem Lifecycle
    // ~~~~~
    // A CaseFileItem instance has the following states: Available, Discarded (Table 8.1).
    // Two states only - unlike PlanItemState, this is intentionally not a Stateless state machine:
    // every mutating transition in Table 8.2 either loops Available -> Available (update, replace,
    // add child, remove child, add reference, remove reference) or is the one-way terminal
    // Available -> Discarded (delete). A guarded enum field captures that shape exactly; a full
    // state-machine library would add ceremony without expressing anything Stateless's transition
    // table would do better for just two states and one terminal edge.
    public enum CaseFileItemState
    {
        Available,
        Discarded
    }
}
