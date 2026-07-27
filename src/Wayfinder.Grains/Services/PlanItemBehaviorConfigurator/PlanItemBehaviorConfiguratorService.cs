using System;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Services.PlanItemStateMachineConfigurator;
using Microsoft.Extensions.Options;

namespace Wayfinder.Grains.Services.PlanItemBehaviorConfigurator
{
    public class PlanItemBehaviorConfiguratorService : IPlanItemBehaviorConfigurator
    {
        private readonly IPlanItemStateMachineConfigurator _planItemStateMachineConfigurator;

        // ADO #67 - Wayfinder ENGINE EXTENSION option (RepetitionGuardOptions), not CMMN spec
        // surface. IOptions<T> parameter defaulted to null (rather than required) so every
        // existing direct `new PlanItemBehaviorConfiguratorService(...)` in the unit test suite
        // keeps compiling unchanged and gets the generous default; production DI (Wayfinder.Silo/
        // Program.cs) and the low-ceiling integration fixture both register a real
        // IOptions<RepetitionGuardOptions> and get that value instead.
        private readonly int _repetitionCeiling;

        public PlanItemBehaviorConfiguratorService(
            IPlanItemStateMachineConfigurator planItemStateMachineConfigurator,
            IOptions<RepetitionGuardOptions> repetitionGuardOptions = null)
        {
            _planItemStateMachineConfigurator = planItemStateMachineConfigurator ?? throw new ArgumentNullException(nameof(planItemStateMachineConfigurator));
            _repetitionCeiling = (repetitionGuardOptions?.Value ?? new RepetitionGuardOptions()).MaxRepetitionsPerPlanItem;
        }

        public async Task<IPlanItemBehavior> Configure(IBehaviorHost host, PlanItemDefinition planItemDefinition)
        {
            var stateMachine = _planItemStateMachineConfigurator.Configure(host.State);
            switch (planItemDefinition)
            {
                case Stage stage when stage.IsCasePlanModel:
                    {
                        var cpmb = new CasePlanModelBehavior(host, stage, stateMachine, _repetitionCeiling);
                        await cpmb.Activate();
                        return cpmb;
                    }
                case Stage stage:
                    {
                        var sb = new StageBehavior(host, stage, stateMachine, _repetitionCeiling);
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
