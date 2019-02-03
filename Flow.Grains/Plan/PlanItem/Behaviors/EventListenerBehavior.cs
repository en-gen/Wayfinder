using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Orleans.Streams;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    public class EventListenerBehavior<TEventListener> : BaseBehavior<TEventListener>
        where TEventListener : EventListener
    {
        public EventListenerBehavior(IBehaviorHost host, TEventListener planItemDefinition, IPlanItemStateMachine stateMachine) :
            base(host, planItemDefinition, stateMachine)
        {
            StateMachine.OnTransitionedAsync(HandleTransitioned);
        }

        private Task HandleTransitioned(PlanItemStateMachine.Transition transition)
        {
            Host.RaiseEvent(new UserCompletableCriteriaMet
            {
                UserCompletable = StateMachine.CanFire(PlanItemTransition.Complete)
            });
            return Task.CompletedTask;
        }

        protected override async Task HandleParentTransitioned(PlanItemTransitionedEvent @event, StreamSequenceToken token = null)
        {
            // ignore if not from direct parent
            if (@event.SourceInstanceId != Host.ParentInstanceId) return;

            PlanItemTransition? transition = null;
            switch (@event.StandardEvent)
            {
                case PlanItemTransition.Suspend:
                case PlanItemTransition.ParentSuspend:
                {
                    Host.RaiseEvent(new ParentSuspended());
                    transition = PlanItemTransition.Suspend;
                    break;
                }
                case PlanItemTransition.Resume:
                case PlanItemTransition.ParentResume:
                {
                    Host.RaiseEvent(new ParentResumed());
                    transition = PlanItemTransition.Resume;
                    break;
                }
                case PlanItemTransition.Exit:
                case PlanItemTransition.Terminate:
                {
                    Host.RaiseEvent(new ParentTerminated());
                    transition = PlanItemTransition.ParentTerminate;
                    break;
                }
            }

            if (transition.HasValue && StateMachine.CanFire(transition.Value))
            {
                await StateMachine.FireAsync(transition.Value);
            }
        }

        protected override Task HandleSentrySatisfied(SentrySatisfiedEvent @event, StreamSequenceToken token = null)
        {
            throw new System.NotImplementedException();
        }
    }
}
