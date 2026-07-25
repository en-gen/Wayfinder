using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;

namespace Flow.Grains.Plan.Case
{
    public interface ICaseDefinitionGrain : Interfaces.Plan.Case.ICaseDefinitionGrain
    {
        Task<PlanItemDefinition> GetPlanItemDefinition(string scope, string definitionId);
    }
}
