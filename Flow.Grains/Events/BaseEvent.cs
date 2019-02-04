using System;

namespace Flow.Grains.Events
{
    [Serializable]
    public abstract class BaseEvent
    {
        public DateTime Ocurred { get; } = DateTime.UtcNow;

        public string SourceScope { get; }
        public string SourceDefinitionId { get; }

        protected BaseEvent(string sourceScope, string sourceDefinitionId)
        {
            SourceScope = sourceScope;
            SourceDefinitionId = sourceDefinitionId;
        }
    }
}
