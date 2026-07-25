using System;
using Wayfinder.Grains.Interfaces.Model;
using Orleans;

namespace Wayfinder.Grains.Events
{
    [GenerateSerializer]
    public class CaseFileItemTransitionedEvent : BaseEvent
    {
        [Id(0)]
        public CaseFileItemTransition StandardEvent { get; }

        public CaseFileItemTransitionedEvent(string caseFileItemId, string caseFileItemRef, CaseFileItemTransition standardEvent) :
            base(caseFileItemId, caseFileItemRef)
        {
            StandardEvent = standardEvent;
        }
    }
}
