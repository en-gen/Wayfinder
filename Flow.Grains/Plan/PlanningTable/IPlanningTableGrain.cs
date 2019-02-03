using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.CmmnElement;

namespace Flow.Grains.Plan.PlanningTable
{
    public interface IPlanningTableGrain : ICmmnElementGrain<Interfaces.Model.PlanningTable>
    {
        Task<DiscretionaryItem[]> GetPlannableItems();
        Task PlanDiscretionaryItem(string discretionaryItemId);
        Task<DiscretionaryItem[]> GetPlannedItems();
    }
}
