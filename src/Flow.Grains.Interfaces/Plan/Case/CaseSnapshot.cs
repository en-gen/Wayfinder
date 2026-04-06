using System;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Interfaces.Plan.PlanItem.Behaviors;

namespace Flow.Grains.Interfaces.Plan.Case
{
    [Serializable]
    public class CaseSnapshot
    {
        public Model.Case Definition { get; set; }
        public Stage CasePlanModel { get; set; }

        public bool UserCompletable { get; set; }
        public bool Required { get; set; }
        public string RequiredEvaluationError { get; set; }

        public bool Repeatable { get; set; }
        public string RepeatableEvaluationError { get; set; }
        public bool Repeated { get; set; }
        public int Repetition { get; set; }

        public bool ManuallyActivatable { get; set; }
        public string ManuallyActivatableEvaluationError { get; set; }

        public PlanItemState PlanItemState { get; set; }
        public PlanItemState? ParentSuspendState { get; set; }

        public CriterionSnapshot EntryCriterionStore { get; set; }
        public CriterionSnapshot ExitCriterionStore { get; set; }

        public StageBehaviorSnapshot BehaviorExtension { get; set; }
    }
}
