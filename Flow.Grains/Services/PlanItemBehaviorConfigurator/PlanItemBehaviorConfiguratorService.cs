using System;
using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Services.PlanItemStateMachineConfigurator;

namespace Flow.Grains.Services.PlanItemBehaviorConfigurator
{
    public class PlanItemBehaviorConfiguratorService : IPlanItemBehaviorConfigurator
    {
        private IPlanItemStateMachineConfigurator PlanItemStateMachineConfigurator { get; }

        public PlanItemBehaviorConfiguratorService(IPlanItemStateMachineConfigurator planItemStateMachineConfigurator)
        {
            PlanItemStateMachineConfigurator = planItemStateMachineConfigurator;
        }

        public async Task<IPlanItemBehavior> Configure(IBehaviorHost host, PlanItemDefinition planItemDefinition)
        {
            var stateMachine = PlanItemStateMachineConfigurator.Configure(host.State);
            switch (planItemDefinition)
            {
                case Stage stage:
                {
                    var sb = new StageBehavior(host, stage, stateMachine);
                    await sb.Activate();
                    return sb;
                }
                case Milestone milestone:
                {
                    var mb = new MilestoneBehavior(host, milestone, stateMachine);
                    await mb.Activate();
                    return mb;
                }
                default:
                {
                    throw new NotImplementedException();
                }
            }
        }
    }
}
