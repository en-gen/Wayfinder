using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Behaviors;

namespace Flow.Grains.Services.PlanItemBehaviorConfigurator
{
    public interface IPlanItemBehaviorConfigurator
    {
        IPlanItemBehavior Configure(IBehaviorHost host, PlanItemDefinition planItemDefinition);
    }
}
