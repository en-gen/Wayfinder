using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Definitions
{
    public interface IPlanItemDefinitionGrain : IGrainWithGuidCompoundKey
    {
        Task Define(PlanItemDefinition planItemDefinition);
        Task<PlanItemDefinition> Definition();
        Task<bool> IsDefined();
    }
}
