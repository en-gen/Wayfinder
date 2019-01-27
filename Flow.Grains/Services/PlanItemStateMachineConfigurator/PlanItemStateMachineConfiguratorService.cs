using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Microsoft.Extensions.Logging;

namespace Flow.Grains.Services.PlanItemStateMachineConfigurator
{
    public class PlanItemStateMachineConfiguratorService : IPlanItemStateMachineConfigurator
    {
        private ILoggerFactory LogFactory { get; }

        public PlanItemStateMachineConfiguratorService(ILoggerFactory logFactory)
        {
            LogFactory = logFactory;
        }

        public IPlanItemStateMachine Configure(PlanItemStore planItemStore) =>
            new PlanItemStateMachine(planItemStore, LogFactory.CreateLogger<PlanItemStateMachine>());
    }
}
