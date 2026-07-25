using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Plan.PlanItem;

namespace Wayfinder.Grains.Plan.PlanItem
{
    public interface IPlanItemInternalGrain : IPlanItemGrain
    {
        Task DefineRepetition(string caseDefinitionId, Interfaces.Model.PlanItem definition, int repetition, string parentDefinitionId, string parentDefinitionScope);
    }
}
