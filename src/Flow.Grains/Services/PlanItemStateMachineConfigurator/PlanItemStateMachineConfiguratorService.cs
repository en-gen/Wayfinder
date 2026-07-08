using System;
using Flow.Grains.Plan;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Microsoft.Extensions.Logging;

namespace Flow.Grains.Services.PlanItemStateMachineConfigurator
{
    public class PlanItemStateMachineConfiguratorService : IPlanItemStateMachineConfigurator
    {
        private readonly ILoggerFactory _logFactory;

        public PlanItemStateMachineConfiguratorService(ILoggerFactory logFactory)
        {
            _logFactory = logFactory ?? throw new ArgumentNullException(nameof(logFactory));
        }

        public IPlanItemStateMachine Configure(IBehaviorStore planItemStore) =>
            new PlanItemStateMachine(planItemStore, _logFactory.CreateLogger<PlanItemStateMachine>());
    }
}
