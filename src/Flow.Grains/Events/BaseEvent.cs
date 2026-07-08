using System;
using Orleans;

namespace Flow.Grains.Events
{
    [GenerateSerializer]
    public abstract class BaseEvent
    {
        [Id(0)]
        public DateTime Occurred { get; } = DateTime.UtcNow;

        [Id(1)]
        public string SourceScope { get; }
        [Id(2)]
        public string SourceDefinitionId { get; }

        protected BaseEvent(string sourceScope, string sourceDefinitionId)
        {
            SourceScope = sourceScope;
            SourceDefinitionId = sourceDefinitionId;
        }
    }
}
