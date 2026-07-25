using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;

namespace Wayfinder.Grains.Plan.Case
{
    public interface ICaseDefinitionGrain : Interfaces.Plan.Case.ICaseDefinitionGrain
    {
        Task<PlanItemDefinition> GetPlanItemDefinition(string scope, string definitionId);
    }
}
