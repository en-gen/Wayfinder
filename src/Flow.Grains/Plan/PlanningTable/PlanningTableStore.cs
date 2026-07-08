using Flow.Grains.Plan.CmmnElement;
using Orleans;

namespace Flow.Grains.Plan.PlanningTable
{
    [GenerateSerializer]
    public class PlanningTableStore : CmmnElementStore<Interfaces.Model.PlanningTable>
    {
    }
}
