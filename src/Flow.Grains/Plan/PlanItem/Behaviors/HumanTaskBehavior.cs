using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Flow.Grains.Plan.PlanningTable;

namespace Flow.Grains.Plan.PlanItem.Behaviors
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
