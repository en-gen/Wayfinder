using Orleans;

namespace Flow.Grains.Events
{
    // D10 - published on the same case-wide stream key (scope + sentry definition id) as
    // SentrySatisfiedEvent, when a sentry's IfPart could not be evaluated at all (as opposed to
    // evaluating cleanly to FALSE) - see SentryGrain.HandleOnPartOccurred/EvaluateIfPart. No
    // subscriber consumes this yet (out of this work item's scope - SentryGrain/SentryStore/its
    // Events only); it exists so the signal is available rather than silently swallowed.
    [GenerateSerializer]
    public class SentryFaultedEvent : BaseEvent
    {
        [Id(0)]
        public string ErrorMessage { get; }

        public SentryFaultedEvent(string scope, string sentryDefinitionId, string errorMessage) :
            base(scope, sentryDefinitionId)
        {
            ErrorMessage = errorMessage;
        }
    }
}
