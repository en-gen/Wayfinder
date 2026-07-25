using Wayfinder.Grains.Plan.CmmnElement;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanningTable
{
    [GenerateSerializer]
    public class PlanningTableStore : CmmnElementStore<Interfaces.Model.PlanningTable>
    {
    }
}
