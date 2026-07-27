using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Orleans;
using Orleans.EventSourcing;
using Orleans.Providers;

namespace Wayfinder.Grains.Tests.Integration.Storage
{
    // Minimal state for the restart-survival proof: primitives plus a bare JsonNode document -
    // the same value shape CaseFileItemStore journals (work item #16) - with no CMMN model
    // involvement. This suite exists purely to prove the storage path, not case semantics.
    [GenerateSerializer]
    public class AzuriteJournalTestState
    {
        [Id(0)]
        public int Counter { get; set; }

        [Id(1)]
        public string LastLabel { get; set; }

        [Id(2)]
        public JsonNode Document { get; set; }

        public void Apply(AzuriteJournalTestEvent @event)
        {
            Counter += @event.Amount;
            LastLabel = @event.Label;
        }

        public void Apply(AzuriteJournalDocumentSet @event)
        {
            Document = @event.Document;
        }
    }

    [GenerateSerializer]
    public class AzuriteJournalTestEvent
    {
        [Id(0)]
        public int Amount { get; set; }

        [Id(1)]
        public string Label { get; set; }
    }

    // JsonNode-bearing journal event: LogStorage persists the event sequence itself, so this is
    // what must survive the grain-storage serializer round-trip (see the serializer pin in
    // AzuriteClusterFixture / Wayfinder.Silo Program.cs - the reflection-JSON default cannot do it).
    [GenerateSerializer]
    public class AzuriteJournalDocumentSet
    {
        [Id(0)]
        public JsonNode Document { get; set; }
    }

    public interface IAzuriteJournalTestGrain : IGrainWithGuidKey
    {
        Task Increment(int amount, string label);

        Task SetDocument(JsonNode document);

        Task<AzuriteJournalTestState> GetState();

        // Forces deactivation so the next call must rehydrate state from the journal -
        // the empirical proof that storage (not just in-memory grain activation) is durable.
        Task DeactivateNow();

        // Issue #154: a per-activation marker that is NOT persisted/journaled (see the
        // _activationId field remarks below) - lets tests prove a call actually landed on a
        // FRESH activation (deterministic proof that deactivation actually happened) without
        // relying on the eventually-consistent grain directory
        // (IManagementGrain.GetActivationAddress), which does not read-your-writes immediately
        // after ForceActivationCollection completes.
        Task<Guid> GetActivationId();
    }

    // Same shape as CmmnElementGrain (Wayfinder.Grains/Plan/CmmnElement/CmmnElementGrain.cs):
    // JournaledGrain<TState> (untyped event base) + [LogConsistencyProvider("LogStorage")],
    // reduced to the minimum needed to exercise the journaled-grain storage path -
    // deliberately CMMN-free.
    [LogConsistencyProvider(ProviderName = "LogStorage")]
    public class AzuriteJournalTestGrain : JournaledGrain<AzuriteJournalTestState>, IAzuriteJournalTestGrain
    {
        // Issue #154: fresh Guid per activation, deliberately NOT part of AzuriteJournalTestState
        // (the [GenerateSerializer] journaled state above) or any raised event - only State is
        // persisted/rehydrated by the log-consistency provider; a plain grain-instance field is
        // invisible to it. Orleans constructs a brand new instance of this class for every
        // activation, so this initializer runs exactly once per activation and can never survive
        // (or be restored across) a deactivation/reactivation cycle the way State does.
        private readonly Guid _activationId = Guid.NewGuid();

        public Task Increment(int amount, string label)
        {
            RaiseEvent(new AzuriteJournalTestEvent { Amount = amount, Label = label });
            return ConfirmEvents();
        }

        public Task SetDocument(JsonNode document)
        {
            RaiseEvent(new AzuriteJournalDocumentSet { Document = document });
            return ConfirmEvents();
        }

        public Task<AzuriteJournalTestState> GetState() => Task.FromResult(State);

        public Task DeactivateNow()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public Task<Guid> GetActivationId() => Task.FromResult(_activationId);
    }
}
