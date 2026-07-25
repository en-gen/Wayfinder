using System;
using Orleans;

namespace Flow.Grains.Events
{
    [GenerateSerializer]
    public class SentrySatisfiedEvent : BaseEvent
    {
        [Id(0)]
        public bool OnPartOccurred { get; }

        public SentrySatisfiedEvent(string scope, string sentryDefinitionId, bool onPartOccurred) :
            base(scope, sentryDefinitionId)
        {
            OnPartOccurred = onPartOccurred;
        }
    }
}
