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

        public IDictionary<string, IDictionary<string, int>> Children { get; set; }
    }
}
