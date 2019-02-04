using System;
using System.Collections.Generic;
using Flow.Grains.Interfaces.Model;

namespace Flow.Grains.Interfaces.Plan.PlanItem
{
    [Serializable]
    public class PlanItemSnapshot
    {
        public Model.PlanItem Definition { get; set; }
        public PlanItemDefinition PlanItemDefinition { get; set; }

        public bool UserCompletable { get; set; }
        public bool Required { get; set; }
        public bool Repeated { get; set; }

        public int Repetition { get; set; }

        public PlanItemState PlanItemState { get; set; }
        public PlanItemState? ParentSuspendState { get; set; }

        public CriterionSnapshot EntryCriterionStore { get; set; }
        public CriterionSnapshot ExitCriterionStore { get; set; }

        public BehaviorSnapshot BehaviorExtension { get; set; }
        
        [Serializable]
        public class CriterionSnapshot
        {
            public string SatisfiedByAddress { get; set; }
            public CriterionState State { get; set; }
        }

        [Serializable]
        public abstract class BehaviorSnapshot
        {
            public TBehaviorStore As<TBehaviorStore>()
                where TBehaviorStore : BehaviorSnapshot => 
                this is TBehaviorStore
                    ? this as TBehaviorStore
                    : null;
        }

        [Serializable]
        public class StageBehaviorSnapshot : BehaviorSnapshot
        {
            // only applicable to PlanItems defined by a Stage
            // PlanItemDefinitionId => PlanItemInstanceId => Repetition
            public IDictionary<string, IDictionary<string, int>> Children { get; set; }
        }

        [Serializable]
        public class TimerEventListenerBehaviorSnapshot : BehaviorSnapshot
        {
        }
    }
}
