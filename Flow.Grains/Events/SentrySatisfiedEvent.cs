using System;

namespace Flow.Grains.Events
{
    [Serializable]
    public class SentrySatisfiedEvent : BaseEvent
    {
        public bool OnPartOccurred { get; }

        public SentrySatisfiedEvent(string scope, string sentryId, bool onPartOccurred) :
            base(scope, sentryId)
        {
            OnPartOccurred = onPartOccurred;
        }
    }
}
