using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.StateMachine;

namespace Flow.Grains.Services.PlanItemStateMachineConfigurator
{
    public interface IPlanItemStateMachineConfigurator
    {
        IPlanItemStateMachine Configure(PlanItemStore planItemStore);
    }
}
