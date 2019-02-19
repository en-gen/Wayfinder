using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.StateMachine;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    public class CasePlanModelBehavior : StageBehavior
    {
        public CasePlanModelBehavior(IBehaviorHost host, Stage planItemDefinition, IPlanItemStateMachine stateMachine) :
            base(host, planItemDefinition, stateMachine)
        {
        }
    }
}
