using System;
using Flow.Grains.Interfaces.Model;

namespace Flow.Grains.Events
{
    [Serializable]
    public class CaseFileItemTransitionedEvent : BaseEvent
    {
        public CaseFileItemTransition StandardEvent { get; }

        public CaseFileItemTransitionedEvent(string caseFileItemId, string caseFileItemRef, CaseFileItemTransition standardEvent) :
            base(caseFileItemId, caseFileItemRef)
        {
            StandardEvent = standardEvent;
        }
    }
}
