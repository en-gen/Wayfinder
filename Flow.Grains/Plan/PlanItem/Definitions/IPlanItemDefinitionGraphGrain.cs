using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Orleans;

namespace Flow.Grains.Plan.PlanItem.Definitions
{
    public interface IPlanItemDefinitionGraphGrain : IGrainWithGuidKey
    {
        Task Construct(Interfaces.Model.Case @case);
        Task<PlanItemDefinition> Find(string scope, string id);
    }
}
