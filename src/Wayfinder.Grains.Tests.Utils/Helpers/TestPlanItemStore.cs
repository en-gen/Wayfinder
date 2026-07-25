using System;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Events;

namespace Flow.Grains.Tests.Utils.Helpers
{
    public class TestPlanItemStore : PlanItemStore
    {
        public TestPlanItemStore(
            string caseDefId = null,
            PlanItem def = null,
            PlanItemDefinition piDef = null,
            PlanItemState initialState = PlanItemState.Available)
        {
            caseDefId = caseDefId ?? Guid.NewGuid().ToString();
            piDef = piDef ?? new Milestone { Id = "Milestone" };
            def = def ?? new PlanItem
            {
                Id = "PlanItem",
                DefinitionRef = piDef.Id
            };

            Apply(new Defined
            {
                CaseDefinitionId = caseDefId,
                PlanItemDefinition = piDef,
                Definition = def
            });
            Apply(new Transitioned
            {
                Destination = initialState
            });
        }
    }
}
