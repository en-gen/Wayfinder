using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.PlanItem.Behaviors;

namespace Wayfinder.Grains.Services.PlanItemBehaviorConfigurator
{
    public interface IPlanItemBehaviorConfigurator
    {
        Task<IPlanItemBehavior> Configure(IBehaviorHost host, PlanItemDefinition planItemDefinition);
    }
}
