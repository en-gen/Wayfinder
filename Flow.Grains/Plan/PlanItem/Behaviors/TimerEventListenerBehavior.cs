using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.StateMachine;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    public class TimerEventListenerBehavior : EventListenerBehavior<TimerEventListener>
    {
        public TimerEventListenerBehavior(IBehaviorHost host, TimerEventListener planItemDefinition, IPlanItemStateMachine stateMachine) :
            base(host, planItemDefinition, stateMachine)
        {
        }
    }
}
