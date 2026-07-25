using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.CmmnElementGrain;

namespace Wayfinder.Grains.Interfaces.Plan.PlanItem
{
    public interface IPlanItemGrain : ICmmnElementGrain<Model.PlanItem>
    {
        Task<PlanItemSnapshot> Trigger(PlanItemTransition transition);
        Task<PlanItemSnapshot> GetSnapshot();
    }
}
