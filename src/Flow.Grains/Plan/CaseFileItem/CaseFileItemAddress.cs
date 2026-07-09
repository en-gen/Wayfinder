namespace Flow.Grains.Plan.CaseFileItem
{
    // Creation surface (work item #16 design decision): CaseFileItem instances are not
    // instantiated top-down from a caseFileModel definition graph the way PlanItems are
    // instantiated from a Stage's PlanItemDefinitions (see CaseDefinitionGrain/StageBehavior) -
    // that model-driven instantiation (5.3's full CaseFile/CaseFileItem definition graph,
    // including children/parent containment and targetRefs/sourceRef reference hierarchies) is
    // explicitly out of scope here; this work item's AC is the event-driven runtime (8.3's
    // lifecycle and the CaseFileItemTransitionedEvent it publishes), not the modeling layer.
    //
    // Instead, a caller (currently: tests; eventually: whatever later work item adds
    // caseFileModel-driven instantiation, or a case worker-facing API) obtains a CaseFileItemGrain
    // handle directly via GrainFactory.GetGrain<ICaseFileItemGrain>(caseInstanceId, address) and
    // calls Create(...) on it explicitly, supplying the CaseFileItem definition and initial value
    // itself. This helper only centralizes the *address scheme* so that scheme lives in one place
    // rather than being a magic string repeated at every call site.
    //
    // 5.3.1 - CaseFile
    // ~~~~~
    // "Every Case is associated with exactly one CaseFile" - unlike PlanItem instances (addressed
    // under their containing Stage's address) CaseFileItem instances are case-scoped, not
    // plan-item-scoped, hence the fixed "casefile" scope root rather than a caller-supplied one.
    // This matches SentryGrain.HandleCaseFileItemTransitioned, which (unlike
    // HandlePlanItemTransitioned) matches purely on SourceRef with no scope-prefix check.
    public static class CaseFileItemAddress
    {
        public const string Scope = "casefile";

        public static string For(string caseFileItemDefinitionId) => $"{Scope}.{caseFileItemDefinitionId}";
    }
}
