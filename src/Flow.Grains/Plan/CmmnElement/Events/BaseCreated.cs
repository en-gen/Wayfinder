using System;

namespace Flow.Grains.Plan.CmmnElement.Events
{
    [Serializable]
    public abstract class BaseCreated
    {
        public DateTime Created { get; set; } = DateTime.UtcNow;
    }
}