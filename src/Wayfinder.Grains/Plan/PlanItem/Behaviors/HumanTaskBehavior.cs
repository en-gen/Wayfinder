using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.PlanItem.StateMachine;
using Wayfinder.Grains.Plan.PlanningTable;

namespace Wayfinder.Grains.Plan.PlanItem.Behaviors
{
    public class HumanTaskBehavior : TaskBehavior<HumanTask>
    {
        public HumanTaskBehavior(IBehaviorHost host, HumanTask planItemDefinition, IPlanItemStateMachine stateMachine) :
            base(host, planItemDefinition, stateMachine)
        {
        }

        protected override async Task Define()
        {
            if (PlanItemDefinition.PlanningTable == null) return;

            var planningTableGrain = Host.GrainFactory
                .GetGrain<IPlanningTableGrain>(Host.CaseInstanceId, Host.InstanceId);

            if (await planningTableGrain.Defined()) return;

            await planningTableGrain.Define(Host.State.CaseDefinitionId, PlanItemDefinition.PlanningTable);
        }
    }
}
