using Wayfinder.Grains.Interfaces.Model;

namespace Wayfinder.Grains.Plan
{
    public interface IBehaviorStore
    {
        bool Defined { get; }
        string CaseDefinitionId { get; }

        PlanItemDefinition PlanItemDefinition { get; }

        bool UserCompletable { get; }
        bool Repeated { get; }
        int Repetition { get; }
        PlanItemState PlanItemState { get; }
        PlanItemState? ParentSuspendState { get; }
        object BehaviorExtension { get; }
    }
}
