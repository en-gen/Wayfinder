using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wayfinder.Grains.Executables;
using Orleans;
using Orleans.Runtime;

namespace Wayfinder.Grains.Scheduler
{
    public interface ITimerEventSchedulerGrain : IGrainWithGuidKey, IRemindable
    {
        Task ScheduleTimer(string planItemInstanceId, Iso8601 schedule, DateTime? timerStart, IDictionary<string, object> context);
        Task CancelTimer(string planItemInstanceId);
    }
}
