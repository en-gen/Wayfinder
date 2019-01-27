using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.CmmnElement;

namespace Flow.Grains.Plan.PlanItem
{
    public interface IPlanItemGrain : ICmmnElementActor<Interfaces.Model.PlanItem>
    {
        Task Trigger(PlanItemTransition transition);
        Task<PlanItemState> GetState();
    }
}
