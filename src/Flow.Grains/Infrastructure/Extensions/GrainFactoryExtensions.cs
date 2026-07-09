using System;
using Flow.Grains.Interfaces.Plan.CaseFileItem;
using Flow.Grains.Plan.CaseFileItem;
using Flow.Grains.Scheduler;
using Orleans;

namespace Flow.Grains.Infrastructure.Extensions
{
    public static class GrainFactoryExtensions
    {
        public static ITimerEventSchedulerGrain GetScheduler(this IGrainFactory grainFactory) =>
            grainFactory.GetGrain<ITimerEventSchedulerGrain>(Guid.Empty);

        // See CaseFileItemAddress for why CaseFileItem instances are addressed under a fixed
        // "casefile" scope rather than a caller-supplied one.
        public static ICaseFileItemGrain GetCaseFileItem(this IGrainFactory grainFactory, Guid caseInstanceId, string caseFileItemDefinitionId) =>
            grainFactory.GetGrain<ICaseFileItemGrain>(caseInstanceId, CaseFileItemAddress.For(caseFileItemDefinitionId));
    }
}
