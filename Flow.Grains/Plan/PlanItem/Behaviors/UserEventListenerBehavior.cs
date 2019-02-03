using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Stateless;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    public class UserEventListenerBehavior : EventListenerBehavior<UserEventListener>
    {
        public UserEventListenerBehavior(IBehaviorHost host, UserEventListener planItemDefinition, IPlanItemStateMachine stateMachine) :
            base(host, planItemDefinition, stateMachine)
        {
            StateMachine.OnTransitionedAsync(HandleTransitioned);
        }

        private async Task HandleTransitioned(StateMachine<PlanItemState, PlanItemTransition>.Transition arg)
        {
            var userCompletable = StateMachine.CanFire(PlanItemTransition.Complete) &&
                                  (PlanItemDefinition.AuthorizedRoleRefs == null ||
                                   !PlanItemDefinition.AuthorizedRoleRefs.Any() ||
                                   true); // case auth

            if (Host.State.UserCompletable == userCompletable) return;

            Host.RaiseEvent(new UserCompletableCriteriaMet
            {
                UserCompletable = userCompletable
            });
            await Host.ConfirmEvents();
        }
    }
}
