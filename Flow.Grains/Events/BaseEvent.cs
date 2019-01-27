using System;

namespace Flow.Grains.Events
{
    [Serializable]
    public abstract class BaseEvent
    {
        public string SourceScope { get; }
        public string SourceId { get; }

        protected BaseEvent(string sourceScope, string sourceId)
        {
            SourceScope = sourceScope;
            SourceId = sourceId;
        }
    }
}
