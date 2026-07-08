using System;
using Orleans;

namespace Flow.Grains.Plan.CmmnElement.Events
{
    [GenerateSerializer]
    public abstract class BaseUpdate
    {
        [Id(0)]
        public DateTime Updated { get; set; } = DateTime.UtcNow;
    }
}