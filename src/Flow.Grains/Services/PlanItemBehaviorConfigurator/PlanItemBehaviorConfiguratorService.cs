using System;
using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Services.PlanItemStateMachineConfigurator;

namespace Flow.Grains.Services.PlanItemBehaviorConfigurator
{
    public class PlanItemBehaviorConfiguratorService : IPlanItemBehaviorConfigurator
    {
        private readonly IPlanItemStateMachineConfigurator _planItemStateMachineConfigurator;

        public PlanItemBehaviorConfiguratorService(IPlanItemStateMachineConfigurator planItemStateMachineConfigurator)
        {
            _planItemStateMachineConfigurator = planItemStateMachineConfigurator ?? throw new ArgumentNullException(nameof(planItemStateMachineConfigurator));
        }

        public async Task<IPlanItemBehavior> Configure(IBehaviorHost host, PlanItemDefinition planItemDefinition)
        {
            var stateMachine = _planItemStateMachineConfigurator.Configure(host.State);
            switch (planItemDefinition)
            {
                case Stage stage when stage.IsCasePlanModel:
                {
                    var cpmb = new CasePlanModelBehavior(host, stage, stateMachine);
                    await cpmb.Activate();
                    return cpmb;
                }
                case Stage stage:
                {
                    var sb = new StageBehavior(host, stage, stateMachine);
                    await sb.Activate();
                    return sb;
                }
                case HumanTask humanTask:
                {
                    var htb = new HumanTaskBehavior(host, humanTask, stateMachine);
                    await htb.Activate();
                    return htb;
                }
                case BaseTask task:
                {
                    var tb = new TaskBehavior<BaseTask>(host, task, stateMachine);
                    await tb.Activate();
                    return tb;
                }
                case Milestone milestone:
                {
                    var mb = new MilestoneBehavior(host, milestone, stateMachine);
                    await mb.Activate();
                    return mb;
                }
                case UserEventListener userEventListener:
                {
                    var uelb = new UserEventListenerBehavior(host, userEventListener, stateMachine);
                    await uelb.Activate();
                    return uelb;
                }
                case TimerEventListener timerEventListener:
                {
                    var telb = new TimerEventListenerBehavior(host, timerEventListener, stateMachine);
                    await telb.Activate();
                    return telb;
                }
                case EventListener eventListener:
                {
                    var elb = new EventListenerBehavior<EventListener>(host, eventListener, stateMachine);
                    await elb.Activate();
                    return elb;
                }
                default:
                {
                    throw new NotImplementedException();
                }
            }
        }
    }
}
