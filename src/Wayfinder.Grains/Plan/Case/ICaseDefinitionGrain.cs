using System.Collections.Generic;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;

namespace Wayfinder.Grains.Plan.Case
{
    public interface ICaseDefinitionGrain : Interfaces.Plan.Case.ICaseDefinitionGrain
    {
        Task<PlanItemDefinition> GetPlanItemDefinition(string scope, string definitionId);

        // Design 05 section A.5 - the whole definition graph in one call, keyed by the same
        // definition-scope address GetPlanItemDefinition searches. Called exactly once per case,
        // at CaseGrain.Create, so the case can pin it; nothing in the running case calls back.
        Task<Dictionary<string, PlanItemDefinition>> GetPlanItemDefinitions();
    }
}
