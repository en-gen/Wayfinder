namespace Flow.Contracts.V1
{
    // ADO #58 - the wire projection of Flow.Grains.Interfaces.Model.CaseFileItemTransition (CMMN
    // 1.1 Table 8.2's CaseFileItem transitions). A deliberate 1:1 mirror kept in lockstep with that
    // enum's members - see PlanItemTransition.cs's remarks for why Flow.Contracts does not simply
    // reference the domain enum. Only Create/Update/Replace are ever actually reported on a
    // CaseFileItemVersionView today (see ValueChanged.Transition's remarks - those are the only
    // value-carrying transitions), but the full vocabulary is mirrored for the same future-proofing
    // reason PlanItemTransition's full set is mirrored rather than a narrowed subset.
    public enum CaseFileItemTransition
    {
        AddChild,
        AddReference,
        Create,
        Delete,
        RemoveChild,
        RemoveReference,
        Replace,
        Update
    }
}
