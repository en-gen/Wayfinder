using System;
using Orleans;

namespace Flow.Grains.Plan.CmmnElement.Events
{
    [GenerateSerializer]
    public abstract class BaseCreated
    {
        [Id(0)]
        public DateTime Created { get; set; } = DateTime.UtcNow;
    }
}