using System;

namespace Flow.Grains.Events
{
    [Serializable]
    public class SentrySatisfiedEvent : BaseEvent
    {
        public bool OnPartOccurred { get; }

        public SentrySatisfiedEvent(string scope, string sentryDefinitionId, bool onPartOccurred) :
            base(scope, sentryDefinitionId)
        {
            OnPartOccurred = onPartOccurred;
        }
    }
}
