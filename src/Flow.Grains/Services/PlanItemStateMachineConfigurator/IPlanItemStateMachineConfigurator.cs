using Flow.Grains.Plan;
using Flow.Grains.Plan.PlanItem.StateMachine;

namespace Flow.Grains.Services.PlanItemStateMachineConfigurator
{
    public interface IPlanItemStateMachineConfigurator
    {
        IPlanItemStateMachine Configure(IBehaviorStore planItemStore);
    }
}
