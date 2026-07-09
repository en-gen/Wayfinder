using System.Collections.Generic;
using System.Text.Json.Nodes;
using Flow.Grains.Interfaces.Plan.CaseFileItem;
using Flow.Grains.Plan.CaseFileItem.Events;
using Flow.Grains.Plan.CmmnElement;
using Orleans;

namespace Flow.Grains.Plan.CaseFileItem
{
    [GenerateSerializer]
    public class CaseFileItemStore : CmmnElementStore<Interfaces.Model.CaseFileItem>
    {
        // 8.3 - CaseFileItem Lifecycle
        // ~~~~~
        // create (Table 8.2) transitions Ø -> Available; CmmnElementStore.Apply(CmmnElementDefined<>)
        // (the Define() call) is this grain's "create" and this field defaults to Available for
        // exactly that reason - there is no separate Apply(Created) event, unlike Discarded below
        // which does need one since it is reached later in the instance's life.
        [Id(0)]
        public CaseFileItemState CaseFileItemState { get; private set; } = CaseFileItemState.Available;

        // 5.3.2 - CaseFileItem attributes, Table 5.17
        // ~~~~~
        // The CaseFileItem's content/data. Modeled as JsonNode (the engine's case-file data shape;
        // see JsonNodeExtensions/JsonObjectInstance in Flow.Grains.Executables) rather than a typed
        // CLR shape, since 5.3.1 states CaseFile "does not imply any assumptions about physical
        // storage of information" and a CaseFileItem "may represent a piece of information of any
        // nature, ranging from unstructured to structured".
        //
        // Persisted as a bare JsonNode - no surrogate/converter needed. The entire JsonNode family
        // (JsonNode/JsonObject/JsonArray/JsonValue and its concrete subclasses) rides the fallback
        // JSON serializer this solution already registers (OrleansFallbackJsonSerializer.AddJsonSerializer -
        // see Flow.Silo/Program.cs and ClusterFixture) UNCONDITIONALLY: Orleans's own JsonCodec hard
        // -codes native support for the JsonNode family (JsonCodec.IsNativelySupportedType, added by
        // dotnet/orleans PR #9605 fixing #9568) regardless of that registration's isSupported
        // predicate - confirmed empirically by round-tripping JsonNode with a predicate that
        // rejects everything (`isSupported: _ => false`) and observing it still serializes
        // correctly. See JsonNodeOrleansSerializationTests for the pinning tests.
        //
        // TRAP: do NOT register an IConverter<JsonNode, TSurrogate> against the abstract JsonNode
        // type to "help" this. That was tried first here and actively broke serialization:
        // Orleans's SurrogateCodec<TField,...> does an exact-type check on write
        // (value.GetType() == typeof(JsonNode), which can never be true for an abstract TField)
        // and falls through to re-dispatching by concrete runtime type to a DIFFERENT codec
        // instance than the one used on read, desyncing the reference table -
        // ReferenceNotFoundException (or CodecNotFoundException for a bare JsonValue), even for a
        // single non-cyclic value. Matches a still-open upstream report, dotnet/orleans#8554, with
        // no maintainer fix. A converter would preempt the native JsonCodec that already handles
        // this correctly and for free.
        //
        // STORAGE: the wire serializer is not the only stack this field crosses - the journaled
        // event log persists through IGrainStorageSerializer, and MemoryGrainStorage's DEFAULT
        // (JsonGrainStorageSerializer) cannot round-trip JsonNode values: a JsonArray anywhere in
        // this state fails the storage write, which the log-consistency protocol retries forever
        // (the grain call never returns). The dev/test silo configuration therefore pins
        // MemoryGrainStorage to OrleansGrainStorageSerializer - see Flow.Silo/Program.cs and
        // ClusterFixture's ConfigureMemoryStorage, and the regression pin in
        // JsonNodeOrleansSerializationTests.GrainCall__Given_JsonNodeArgumentWithArray.
        [Id(1)]
        public JsonNode Value { get; private set; }

        // 5.3.2 - CaseFileItem attributes, Table 5.17: children / targetRefs.
        // ~~~~~
        // Tracked as bare id sets (mirroring SentryStore._occurredOnPartIds's Id-tracking
        // rationale) rather than resolved CaseFileItem object graphs: this work item's scope is
        // the runtime transition/event surface (8.3), not the caseFileModel containment/reference
        // navigation layer (5.3.2's full children/parent/targetRefs/sourceRef graph, and 8.3.1's
        // getCaseFileItemInstance* navigation operations) - see ICaseFileItemGrain's remarks.
        [Id(2)]
        private readonly ICollection<string> _childCaseFileItemIds = new HashSet<string>();
        public IEnumerable<string> ChildCaseFileItemIds => _childCaseFileItemIds;

        [Id(3)]
        private readonly ICollection<string> _targetCaseFileItemIds = new HashSet<string>();
        public IEnumerable<string> TargetCaseFileItemIds => _targetCaseFileItemIds;

        public void Apply(ValueChanged @event)
        {
            Updated = @event.Updated;
            Value = @event.Value;
        }

        public void Apply(ChildAdded @event)
        {
            Updated = @event.Updated;
            _childCaseFileItemIds.Add(@event.ChildCaseFileItemId);
        }

        public void Apply(ChildRemoved @event)
        {
            Updated = @event.Updated;
            _childCaseFileItemIds.Remove(@event.ChildCaseFileItemId);
        }

        public void Apply(ReferenceAdded @event)
        {
            Updated = @event.Updated;
            _targetCaseFileItemIds.Add(@event.TargetCaseFileItemId);
        }

        public void Apply(ReferenceRemoved @event)
        {
            Updated = @event.Updated;
            _targetCaseFileItemIds.Remove(@event.TargetCaseFileItemId);
        }

        public void Apply(Discarded @event)
        {
            Updated = @event.Updated;
            CaseFileItemState = CaseFileItemState.Discarded;
        }
    }
}
