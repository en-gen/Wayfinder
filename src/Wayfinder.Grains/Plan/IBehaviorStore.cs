using Wayfinder.Grains.Interfaces.Model;

namespace Wayfinder.Grains.Plan
{
    public interface IBehaviorStore
    {
        bool Defined { get; }
        string CaseDefinitionId { get; }

        PlanItemDefinition PlanItemDefinition { get; }

        // Design 05 section A.5 - the case model as resolved at CaseGrain.Create, threaded down
        // the plan-item tree and journaled here so nothing in a running case reaches back to
        // ICaseDefinitionGrain. Null for elements defined outside a case Create flow (e.g.
        // PlanItemGrain's bare 2-arg Define overload, used only by test scaffolding).
        CaseModelPin Pin { get; }

        bool UserCompletable { get; }
        bool Repeated { get; }
        int Repetition { get; }
        PlanItemState PlanItemState { get; }
        PlanItemState? ParentSuspendState { get; }
        object BehaviorExtension { get; }
    }
}
