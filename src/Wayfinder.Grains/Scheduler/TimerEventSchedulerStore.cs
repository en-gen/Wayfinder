using System;
using System.Collections.Generic;
using Orleans;
using Quartz;

namespace Wayfinder.Grains.Scheduler
{
    [GenerateSerializer]
    public class TimerEventSchedulerStore
    {
        [Id(0)]
        public IDictionary<string, JobKey> JobKeys { get; } = new Dictionary<string, JobKey>();
    }
}
