using System;
using System.Collections.Generic;
using Quartz;

namespace Flow.Grains.Scheduler
{
    [Serializable]
    public class TimerEventSchedulerStore
    {
        public IDictionary<string, JobKey> JobKeys { get; } = new Dictionary<string, JobKey>();
    }
}
