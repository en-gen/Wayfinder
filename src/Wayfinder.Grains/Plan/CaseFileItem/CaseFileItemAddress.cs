namespace Wayfinder.Grains.Plan.CaseFileItem
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

        // D3 - 8.5's last sentence: "Sentries with no OnPart must have an IfPart, and that IfPart
        // will be evaluated for all CaseFileItem events because IfPart expressions are based on
        // CaseFileItem properties." A standalone-IfPart Sentry (Definition.OnParts.Count == 0) has
        // no per-item sourceRef to subscribe to - CaseFileItemGrain.PublishTransition additionally
        // publishes every transition here, a case-wide stream keyed by this fixed sentinel rather
        // than any specific CaseFileItem id, which such a Sentry subscribes to instead (see
        // SentryGrain.SubscribeToOnPartTransitions). Chosen over enumerating every CaseFileItem id
        // from the Case's caseFileModel (topology (a)): CaseFileItem instances are created ad-hoc,
        // not from a model-driven definition graph (see this class's other remarks, from #16) - no
        // such enumerable list of "every CaseFileItem id for this case" exists to walk, and new
        // items can appear at any time during a case's life, which a fixed-at-subscription-time
        // per-id fan-out could never keep up with. A case-wide stream needs no such enumeration and
        // naturally covers dynamically-created items.
        public const string CaseWideSentinel = "casefile.*";
    }
}
