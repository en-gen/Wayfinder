using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.CmmnElementGrain;
using Wayfinder.Grains.Plan.CmmnElement;

namespace Wayfinder.Grains.Plan.PlanningTable
{
    public interface IPlanningTableGrain : ICmmnElementGrain<Interfaces.Model.PlanningTable>
    {
        Task<DiscretionaryItem[]> GetPlannableItems();
    }
}
