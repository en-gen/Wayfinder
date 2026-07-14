using System;
using Flow.Grains.Interfaces.Plan.CaseFileItem;
using Flow.Grains.Plan.CaseFileItem;
using Flow.Grains.Scheduler;
using Orleans;

namespace Flow.Grains.Infrastructure.Extensions
{
    public static class GrainFactoryExtensions
    {
        // #31 (findings B2/B3): this used to be grainFactory.GetGrain<ITimerEventSchedulerGrain>
        // (Guid.Empty) - a fixed, unkeyed primary key. ITimerEventSchedulerGrain is
        // IGrainWithGuidKey, so Guid.Empty addressed exactly ONE grain activation shared by every
        // case in the cluster: a scheduler that should be scoped per case instance was instead a
        // silent process-wide singleton. Had anything ever called this helper, every case's timers
        // would have been tracked (and, on CancelTimer, collided) in that single shared grain's
        // State.JobKeys dictionary. The correct key is the case instance id - ITimerEventSchedulerGrain
        // is addressed per-case, with per-plan-item granularity handled inside the grain via Quartz
        // job/trigger identities ($"{caseInstanceId}_{planItemInstanceId}" - see
        // TimerEventSchedulerGrain.ConfigureJob), not via a compound Orleans key. This helper had no
        // callers (TimerEventListenerBehavior inlined the correct GetGrain<ITimerEventSchedulerGrain>
        // (caseInstanceId) call at each of its 3 use sites instead) - it was dead code, but a landmine
        // for the next caller who reached for the obvious-looking shortcut. Fixed and now the single
        // source of truth all 3 call sites route through.
        public static ITimerEventSchedulerGrain GetScheduler(this IGrainFactory grainFactory, Guid caseInstanceId) =>
            grainFactory.GetGrain<ITimerEventSchedulerGrain>(caseInstanceId);

        // See CaseFileItemAddress for why CaseFileItem instances are addressed under a fixed
        // "casefile" scope rather than a caller-supplied one.
        public static ICaseFileItemGrain GetCaseFileItem(this IGrainFactory grainFactory, Guid caseInstanceId, string caseFileItemDefinitionId) =>
            grainFactory.GetGrain<ICaseFileItemGrain>(caseInstanceId, CaseFileItemAddress.For(caseFileItemDefinitionId));
    }
}
