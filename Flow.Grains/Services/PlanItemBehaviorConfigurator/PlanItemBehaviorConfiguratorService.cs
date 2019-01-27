using System;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Behaviors;

namespace Flow.Grains.Services.PlanItemBehaviorConfigurator
{
    public class PlanItemBehaviorConfiguratorService : IPlanItemBehaviorConfigurator
    {
        public IPlanItemBehavior Configure(IBehaviorHost host, PlanItemDefinition planItemDefinition)
        {
            switch (planItemDefinition)
            {
                case Milestone milestone:
                {
                    return new MilestoneBehavior(host, milestone);
                }
                default:
                {
                    throw new NotImplementedException();
                }
            }
        }
    }
}
