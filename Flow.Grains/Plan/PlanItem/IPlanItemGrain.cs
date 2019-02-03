using System;
using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Plan.CmmnElement;

namespace Flow.Grains.Plan.PlanItem
{
    public interface IPlanItemGrain : ICmmnElementGrain<Interfaces.Model.PlanItem>
    {
        Task DefineRepetition(Guid caseDefinitionId, Interfaces.Model.PlanItem definition, int repetition);
        Task<PlanItemSnapshot> Trigger(PlanItemTransition transition);
        Task<PlanItemSnapshot> GetSnapshot();
    }
}
