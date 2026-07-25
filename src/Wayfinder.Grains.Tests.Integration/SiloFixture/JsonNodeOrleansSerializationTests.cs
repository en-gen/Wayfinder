using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Serialization;
using Xunit;
using CaseFileItemStore = Wayfinder.Grains.Plan.CaseFileItem.CaseFileItemStore;
using ValueChanged = Wayfinder.Grains.Plan.CaseFileItem.Events.ValueChanged;

namespace Wayfinder.Grains.Tests.Integration.SiloFixture
{
    // Pinning tests for System.Text.Json.Nodes.JsonNode through Orleans's actual registered
    // serializer (see OrleansFallbackJsonSerializerTests for the sibling pinning test covering the
    // CMMN model's own fallback serializer). Three findings are pinned here, all verified
    // empirically during work item #16:
    //
    // 1. WIRE + COPY: JsonNode needs NO surrogate, converter, or predicate change of any kind.
    //    Microsoft.Orleans.Serialization.SystemTextJson's JsonCodec hard-codes native support for
    //    the entire JsonNode family - JsonNode/JsonObject/JsonArray/JsonValue and its concrete
    //    subclasses - via JsonCodec.IsNativelySupportedType (added by dotnet/orleans PR #9605,
    //    fixing #9568), UNCONDITIONALLY: that native-type check runs before, and independent of,
    //    the isSupported predicate this solution's own OrleansFallbackJsonSerializer.AddJsonSerializer
    //    registration passes. Confirmed in isolation: even with isSupported: _ => false (a
    //    predicate rejecting every type), a bare JsonNode still round-trips correctly.
    //
    // 2. TRAP - do not "fix" this with a surrogate. An IConverter<JsonNode, TSurrogate> against
    //    the abstract JsonNode type was tried first here and actively broke serialization:
    //    SurrogateCodec<TField,...> only supports a concrete, non-polymorphic TField - a concrete
    //    JsonObject/JsonArray/JsonValue instance arriving at the abstract-typed codec gets
    //    re-dispatched by runtime type to a DIFFERENT codec than the one used on read, desyncing
    //    the reference table - ReferenceNotFoundException (or CodecNotFoundException for a bare
    //    JsonValue), even for a single non-cyclic value. Matches the still-open upstream report
    //    dotnet/orleans#8554. A converter would also preempt the native JsonCodec that already
    //    handles this correctly and for free.
    //
    // 3. STORAGE: the wire serializer is NOT the only serializer JsonNode-bearing grain state
    //    crosses. Journaled-grain state is persisted through IGrainStorageSerializer, and
    //    MemoryGrainStorage's DEFAULT is JsonGrainStorageSerializer - a separate reflection-based
    //    JSON stack that cannot round-trip JsonNode values (a JsonArray anywhere in the journaled
    //    state fails the storage write, which the log-consistency protocol retries indefinitely -
    //    the grain call never returns; observed as a 30s client timeout with the activation
    //    spinning at ~6k scheduler work items/sec). Fixed by pinning MemoryGrainStorage to
    //    OrleansGrainStorageSerializer in ClusterFixture + Wayfinder.Silo/Program.cs, so storage rides
    //    the same Orleans serializer as the wire. GrainCall__Given_JsonNodeArgumentWithArray below
    //    is the regression pin: it hangs, not fails, if storage regresses to the JSON storage
    //    serializer.
    //
    // Assertions check content (.GetValue<T>()/.ToJsonString()), never runtime type:
    // JsonValue.Create("s") deserializes as JsonValueOfElement, a different internal concrete
    // subclass than the JsonValuePrimitive<string> it started as - content equality is what's
    // contractually guaranteed, not type identity.
    [Collection(ClusterCollection.Name)]
    public class JsonNodeOrleansSerializationTests
    {
        private readonly Serializer _serializer;
        private readonly DeepCopier _copier;
        private readonly IClusterClient _clusterClient;

        public JsonNodeOrleansSerializationTests(ClusterFixture fixture)
        {
            _serializer = fixture.Cluster.ServiceProvider.GetRequiredService<Serializer>();
            _copier = fixture.Cluster.ServiceProvider.GetRequiredService<DeepCopier>();
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        [Fact]
        public void RoundTrip__Given_JsonObjectWithMixedValueKinds__Then_DeserializesWithSameContent()
        {
            JsonNode original = new JsonObject
            {
                ["name"] = "Alice",
                ["age"] = 42,
                ["active"] = true,
                ["nested"] = new JsonObject { ["x"] = 1 },
                ["tags"] = new JsonArray("a", "b", "c")
            };

            var bytes = _serializer.SerializeToArray(original);
            var roundTripped = _serializer.Deserialize<JsonNode>(bytes);

            roundTripped.Should().NotBeNull();
            roundTripped["name"].GetValue<string>().Should().Be("Alice");
            roundTripped["age"].GetValue<int>().Should().Be(42);
            roundTripped["active"].GetValue<bool>().Should().BeTrue();
            roundTripped["nested"]["x"].GetValue<int>().Should().Be(1);
            roundTripped["tags"].AsArray().Should().HaveCount(3);
            roundTripped["tags"][1].GetValue<string>().Should().Be("b");
        }

        [Fact]
        public void RoundTrip__Given_JsonValueString__Then_DeserializesWithSameContent()
        {
            JsonNode original = JsonValue.Create("hello world");

            var bytes = _serializer.SerializeToArray(original);
            var roundTripped = _serializer.Deserialize<JsonNode>(bytes);

            // content, not runtime type - see class remarks on JsonValueOfElement.
            roundTripped.GetValue<string>().Should().Be("hello world");
        }

        [Fact]
        public void RoundTrip__Given_NullJsonNode__Then_DeserializesAsNull()
        {
            JsonNode original = null;

            var bytes = _serializer.SerializeToArray(original);
            var roundTripped = _serializer.Deserialize<JsonNode>(bytes);

            roundTripped.Should().BeNull();
        }

        // CaseFileItemStore.Value is nested inside a [GenerateSerializer]-attributed grain state
        // type (not serialized as a bare JsonNode at the top level) - this pins that native
        // support also applies when nested inside another native-serialized type, which is the
        // shape actually exercised by the grain (see CaseFileItemGrainTests).
        [Fact]
        public void RoundTrip__Given_JsonNodeNestedInGeneratedType__Then_DeserializesWithSameContent()
        {
            var original = new CaseFileItemStore();
            original.Apply(new ValueChanged
            {
                Value = new JsonObject { ["nested"] = "value" }
            });

            var bytes = _serializer.SerializeToArray(original);
            var roundTripped = _serializer.Deserialize<CaseFileItemStore>(bytes);

            roundTripped.Value["nested"].GetValue<string>().Should().Be("value");
        }

        // The deep-copy path - what Orleans uses instead of serialization for in-process
        // grain-to-grain call arguments and raised-event isolation. As of this work item no
        // PRODUCTION code path passes a JsonNode grain-to-grain (CaseFileItemGrain is only ever
        // called from client code today; work item #17's ExpressionGrain binding is the only
        // sketched future caller - see ExpressionGrain.cs's commented-out GetCaseFileItem sketch),
        // so this pins the copier mechanism itself, both bare and nested in a journaled event.
        [Fact]
        public void Copy__Given_JsonNodeFamily__Then_DeepCopiesWithSameContent()
        {
            JsonNode bare = new JsonObject
            {
                ["kind"] = "probe",
                ["items"] = new JsonArray(1, 2, 3)
            };

            var bareCopy = _copier.Copy(bare);
            bareCopy.Should().NotBeSameAs(bare);
            bareCopy.ToJsonString().Should().Be(bare.ToJsonString());

            var @event = new ValueChanged { Value = new JsonArray("a", "b") };
            var eventCopy = _copier.Copy(@event);
            eventCopy.Should().NotBeSameAs(@event);
            eventCopy.Value.ToJsonString().Should().Be(@event.Value.ToJsonString());
        }

        // Exercises JsonNode as an actual grain-call argument through a real grain that JOURNALS
        // the value (finding 3 in the class remarks): the value crosses the wire serializer
        // (client->silo), then the storage serializer (journal write via ConfirmEvents). The
        // JsonArray in the payload is the load-bearing part - MemoryGrainStorage's default JSON
        // storage serializer cannot round-trip it, and this test HANGS (30s timeout, storage
        // retry-forever) rather than failing if the OrleansGrainStorageSerializer pin in
        // ClusterFixture/Program.cs is ever removed.
        [Fact]
        public async Task GrainCall__Given_JsonNodeArgumentWithArray__Then_JournalsAndReadsBack()
        {
            var caseInstanceId = Guid.NewGuid();
            const string caseFileItemId = "JsonNodeCodecProbe";

            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            var value = new JsonObject
            {
                ["kind"] = "probe",
                ["items"] = new JsonArray(1, 2, 3)
            };

            await subject.Create(
                $"caseDef-{ShortGuid.NewGuid()}",
                new Interfaces.Model.CaseFileItem { Id = caseFileItemId },
                value);

            var snapshot = await subject.GetSnapshot();

            snapshot.Value["kind"].GetValue<string>().Should().Be("probe");
            snapshot.Value["items"].AsArray().Should().HaveCount(3);
        }
    }
}
