using Wayfinder.Grains.Plan;
using Wayfinder.Grains.Plan.PlanItem.StateMachine;

namespace Wayfinder.Grains.Services.PlanItemStateMachineConfigurator
{
    public interface IPlanItemStateMachineConfigurator
    {
        IPlanItemStateMachine Configure(IBehaviorStore planItemStore);
    }
}
