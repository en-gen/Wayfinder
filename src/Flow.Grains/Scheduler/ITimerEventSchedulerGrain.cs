using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flow.Grains.Executables;
using Orleans;
using Orleans.Runtime;

namespace Flow.Grains.Scheduler
{
    public interface ITimerEventSchedulerGrain: IGrainWithGuidKey, IRemindable
    {
        Task ScheduleTimer(string planItemInstanceId, Iso8601 schedule, DateTime? timerStart, IDictionary<string, object> context);
        Task CancelTimer(string planItemInstanceId);
    }
}
