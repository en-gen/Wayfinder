using System;
using Orleans;

namespace Flow.Grains.Interfaces.Plan.CaseFileItem
{
    // ADO #58 - the curated, typed history surface ICaseFileItemGrain.GetHistory returns. Not the
    // raw journal (see ICmmnElementGrain.GetJournaledEvents's remarks - that seam predates this
    // work item and stays as-is for the actor-stamping replay-safety tests it was built for): one
    // descriptor per journaled event that carries a Value change (Create/Update/Replace, all
    // raised as CaseFileItem.Events.ValueChanged - see CaseFileItemGrain.Create/ChangeValue).
    // ChildAdded/ChildRemoved/ReferenceAdded/ReferenceRemoved/Discarded don't carry a Value and so
    // never produce a descriptor here, even though they still consume a journal sequence number
    // (GetValueAt(version) still accounts for them when replaying).
    [GenerateSerializer]
    public class CaseFileItemVersionDescriptor
    {
        // The 1-based journal sequence number of the underlying ValueChanged event - i.e. the Nth
        // event RetrieveConfirmedEvents(0, Version) would return (see CaseFileItemGrain.
        // GetHistory/GetValueAt). Not a separate "version 1, 2, 3..." counter over only the
        // value-carrying events: a caller can feed this number straight into GetValueAt(version)
        // and get back exactly the value this descriptor reports, and it stays meaningful
        // alongside the raw GetJournaledEvents() seam other tests already use.
        [Id(0)]
        public int Version { get; set; }

        [Id(1)]
        public DateTime UpdatedUtc { get; set; }

        // ADO #59's already-stamped actor fields (IActorStampedEvent), copied verbatim from the
        // underlying ValueChanged event. Default (Guid.Empty/User/null) for any event raised
        // before #59 shipped - see ActorStampingReplaySafetyTests for why that default is safe
        // and expected, not a fault.
        [Id(2)]
        public Guid ActorPrincipalId { get; set; }
        [Id(3)]
        public ActorPrincipalType ActorPrincipalType { get; set; }
        [Id(4)]
        public string ActorOnBehalfOf { get; set; }

        // Which of the three value-carrying CaseFileItemTransitions (Create/Update/Replace)
        // produced this version - see ValueChanged.Transition's remarks. Null for any
        // ValueChanged event raised before ADO #58 shipped (that field did not exist yet) -
        // deliberately left null rather than guessed, see
        // CaseFileItemVersionHistoryReplaySafetyTests.
        [Id(5)]
        public Model.CaseFileItemTransition? Transition { get; set; }
    }
}
