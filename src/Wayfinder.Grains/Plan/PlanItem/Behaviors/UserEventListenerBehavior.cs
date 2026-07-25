using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Plan.PlanItem.StateMachine;
using Wayfinder.Grains.Plan.Role;
using Orleans;
using Stateless;

namespace Wayfinder.Grains.Plan.PlanItem.Behaviors
{
    public class UserEventListenerBehavior : EventListenerBehavior<UserEventListener>
    {
        public UserEventListenerBehavior(IBehaviorHost host, UserEventListener planItemDefinition, IPlanItemStateMachine stateMachine) :
            base(host, planItemDefinition, stateMachine)
        {
        }

        protected override async Task HandleTransitioned(StateMachine<PlanItemState, PlanItemTransition>.Transition transition)
        {
            var userCompletable = Host.State.PlanItemState == PlanItemState.Available &&
                                  ((PlanItemDefinition.AuthorizedRoleRefs?.Length ?? 0) == 0 ||
                                   await Authorized());

            if (Host.State.UserCompletable == userCompletable) return;

            Host.RaiseEvent(new UserCompletableCriteriaMet
            {
                UserCompletable = userCompletable
            });
            await Host.ConfirmEvents();
        }

        // this will check all roles in parallel.  as soon
        // as a task returns true, the remaining tasks are canceled
        // and result is returned
        private async Task<bool> Authorized()
        {
            var tcs = new GrainCancellationTokenSource();

            var remainingTasks = PlanItemDefinition.AuthorizedRoleRefs
                .Select(roleId => Host.GrainFactory
                    .GetGrain<IRoleGrain>(Host.CaseInstanceId, roleId)
                    .Authorize(tcs.Token))
                .ToHashSet();

            while (remainingTasks.Any())
            {
                var next = await Task.WhenAny(remainingTasks);
                if (next.Result)
                {
                    await tcs.Cancel();
                    tcs.Dispose();
                    return true;
                }
                remainingTasks.Remove(next);
            }

            return false;
        }
    }
}
