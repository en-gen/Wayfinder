using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Orleans;

namespace Flow.Grains.Plan.PlanItem.Definitions
{
    public interface IPlanItemDefinitionGrain : IGrainWithGuidCompoundKey
    {
        Task Define(PlanItemDefinition planItemDefinition);
        Task<PlanItemDefinition> Definition();
        Task<bool> IsDefined();
    }
}
