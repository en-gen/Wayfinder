using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Behaviors;

namespace Flow.Grains.Services.PlanItemBehaviorConfigurator
{
    public interface IPlanItemBehaviorConfigurator
    {
        Task<IPlanItemBehavior> Configure(IBehaviorHost host, PlanItemDefinition planItemDefinition);
    }
}
