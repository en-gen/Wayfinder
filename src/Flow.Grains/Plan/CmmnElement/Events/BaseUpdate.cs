using System;

namespace Flow.Grains.Plan.CmmnElement.Events
{
    [Serializable]
    public abstract class BaseUpdate
    {
        public DateTime Updated { get; set; } = DateTime.UtcNow;
    }
}