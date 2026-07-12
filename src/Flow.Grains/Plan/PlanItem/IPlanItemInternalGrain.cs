using System.Threading.Tasks;
using Flow.Grains.Interfaces.Plan.PlanItem;

namespace Flow.Grains.Plan.PlanItem
{
    public interface IPlanItemInternalGrain : IPlanItemGrain
    {
        Task DefineRepetition(string caseDefinitionId, Interfaces.Model.PlanItem definition, int repetition, string parentDefinitionId, string parentDefinitionScope);
    }
}
