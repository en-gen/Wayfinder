using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.CmmnElementGrain;

namespace Flow.Grains.Interfaces.Plan.PlanItem
{
    public interface IPlanItemGrain : ICmmnElementGrain<Model.PlanItem>
    {
        Task<PlanItemSnapshot> Trigger(PlanItemTransition transition);
        Task<PlanItemSnapshot> GetSnapshot();
    }
}
