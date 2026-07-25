using System;
using Flow.Grains.Interfaces.Model;
using Orleans;

namespace Flow.Grains.Interfaces.Plan.PlanItem
{
    [GenerateSerializer]
    public class PlanItemSnapshot
    {
        [Id(0)]
        public Model.PlanItem Definition { get; set; }
        [Id(1)]
        public PlanItemDefinition PlanItemDefinition { get; set; }

        [Id(2)]
        public bool UserCompletable { get; set; }
        [Id(3)]
        public bool Required { get; set; }
        [Id(4)]
        public string RequiredEvaluationError { get; set; }

        [Id(5)]
        public bool Repeatable { get; set; }
        [Id(6)]
        public string RepeatableEvaluationError { get; set; }
        [Id(7)]
        public bool Repeated { get; set; }
        [Id(8)]
        public int Repetition { get; set; }

        [Id(9)]
        public bool ManuallyActivatable { get; set; }
        [Id(10)]
        public string ManuallyActivatableEvaluationError { get; set; }

        [Id(11)]
        public PlanItemState PlanItemState { get; set; }
        [Id(12)]
        public PlanItemState? ParentSuspendState { get; set; }

        [Id(13)]
        public CriterionSnapshot EntryCriterionStore { get; set; }
        [Id(14)]
        public CriterionSnapshot ExitCriterionStore { get; set; }

        [Id(15)]
        public object BehaviorExtension { get; set; }
    }
}
