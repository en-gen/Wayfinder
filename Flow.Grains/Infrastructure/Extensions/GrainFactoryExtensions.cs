using System;
using Flow.Grains.Scheduler;
using Orleans;

namespace Flow.Grains.Infrastructure.Extensions
{
    public static class GrainFactoryExtensions
    {
        public static ITimerEventSchedulerGrain GetScheduler(this IGrainFactory grainFactory) =>
            grainFactory.GetGrain<ITimerEventSchedulerGrain>(Guid.Empty);
    }
}
