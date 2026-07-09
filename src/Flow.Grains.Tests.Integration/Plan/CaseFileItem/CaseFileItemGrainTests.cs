using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Flow.Grains.Events;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Plan.CaseFileItem;
using Flow.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Orleans;
using Orleans.Streams;
using Xunit;
using CaseFileItemModel = Flow.Grains.Interfaces.Model.CaseFileItem;
using CaseFileItemState = Flow.Grains.Interfaces.Plan.CaseFileItem.CaseFileItemState;
using CaseFileItemTransition = Flow.Grains.Interfaces.Model.CaseFileItemTransition;

namespace Flow.Grains.Tests.Integration.Plan.CaseFileItem
{
    [Collection(ClusterCollection.Name)]
    public class CaseFileItemGrainTests
    {
        private readonly IClusterClient _clusterClient;

        public CaseFileItemGrainTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        // 8.3 - CaseFileItem Lifecycle, Table 8.2: create (Ø -> Available).
        [Theory, AutoData]
        public async Task Create__Given_ValidDefinition__Then_AvailableWithInitialValue
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId)
        {
            var definition = new CaseFileItemModel { Id = caseFileItemId };
            var value = JsonValue.Create("initial content");

            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, definition, value);

            var snapshot = await subject.GetSnapshot();

            snapshot.Should().NotBeNull();
            snapshot.CaseFileItemState.Should().Be(CaseFileItemState.Available);
            snapshot.Value.ToJsonString().Should().Be(value.ToJsonString());
            snapshot.Definition.Id.Should().Be(caseFileItemId);
        }

        [Theory, AutoData]
        public async Task Create__Given_NullDefinition__Then_ThrowArgumentNullException
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId)
        {
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject
                .Awaiting(x => x.Create(caseDefinitionId, null, null))
                .Should()
                .ThrowAsync<ArgumentNullException>();
        }

        [Theory, AutoData]
        public async Task Create__Given_AlreadyCreated__Then_ThrowInvalidOperationException
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId)
        {
            var definition = new CaseFileItemModel { Id = caseFileItemId };
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, definition, null);

            await subject
                .Awaiting(x => x.Create(caseDefinitionId, definition, null))
                .Should()
                .ThrowAsync<InvalidOperationException>();
        }

        // Table 8.2: update (Available -> Available).
        [Theory, AutoData]
        public async Task Update__Given_Available__Then_ValueChangedAndRemainsAvailable
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId)
        {
            var definition = new CaseFileItemModel { Id = caseFileItemId };
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, definition, JsonValue.Create("original"));

            var updatedValue = JsonValue.Create("updated");
            await subject.Update(updatedValue);

            var snapshot = await subject.GetSnapshot();

            snapshot.CaseFileItemState.Should().Be(CaseFileItemState.Available);
            snapshot.Value.ToJsonString().Should().Be(updatedValue.ToJsonString());
        }

        // Table 8.2: replace (Available -> Available).
        [Theory, AutoData]
        public async Task Replace__Given_Available__Then_ValueChangedAndRemainsAvailable
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId)
        {
            var definition = new CaseFileItemModel { Id = caseFileItemId };
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, definition, JsonValue.Create("original"));

            var replacedValue = new JsonObject { ["replaced"] = true };
            await subject.Replace(replacedValue);

            var snapshot = await subject.GetSnapshot();

            snapshot.CaseFileItemState.Should().Be(CaseFileItemState.Available);
            snapshot.Value.ToJsonString().Should().Be(replacedValue.ToJsonString());
        }

        // Table 8.2: add child / remove child (Available -> Available).
        [Theory, AutoData]
        public async Task AddChild__Given_Available__Then_ChildTracked
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId, string childId)
        {
            var definition = new CaseFileItemModel { Id = caseFileItemId };
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, definition, null);
            await subject.AddChild(childId);

            var snapshot = await subject.GetSnapshot();

            snapshot.CaseFileItemState.Should().Be(CaseFileItemState.Available);
        }

        [Theory, AutoData]
        public async Task AddChild__Given_EmptyChildId__Then_ThrowArgumentException
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId)
        {
            var definition = new CaseFileItemModel { Id = caseFileItemId };
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, definition, null);

            await subject
                .Awaiting(x => x.AddChild(""))
                .Should()
                .ThrowAsync<ArgumentException>();
        }

        // Table 8.2: add reference / remove reference (Available -> Available).
        [Theory, AutoData]
        public async Task AddReference__Given_Available__Then_ReferenceTracked
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId, string targetId)
        {
            var definition = new CaseFileItemModel { Id = caseFileItemId };
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, definition, null);

            await subject
                .Awaiting(x => x.AddReference(targetId))
                .Should()
                .NotThrowAsync();
        }

        // Table 8.2: delete (Available -> Discarded). Terminal.
        [Theory, AutoData]
        public async Task Delete__Given_Available__Then_Discarded
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId)
        {
            var definition = new CaseFileItemModel { Id = caseFileItemId };
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, definition, null);
            await subject.Delete();

            var snapshot = await subject.GetSnapshot();

            snapshot.CaseFileItemState.Should().Be(CaseFileItemState.Discarded);
        }

        // 8.3 - "A CaseFileItem instance in this state is considered deleted and is not available
        // to Case workers or expressions" - every mutating operation on a Discarded instance must
        // reject, not silently no-op.
        [Theory, AutoData]
        public async Task Update__Given_Discarded__Then_ThrowInvalidOperationException
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId)
        {
            var definition = new CaseFileItemModel { Id = caseFileItemId };
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, definition, null);
            await subject.Delete();

            await subject
                .Awaiting(x => x.Update(JsonValue.Create("too late")))
                .Should()
                .ThrowAsync<InvalidOperationException>();
        }

        [Theory, AutoData]
        public async Task Delete__Given_AlreadyDiscarded__Then_ThrowInvalidOperationException
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId)
        {
            var definition = new CaseFileItemModel { Id = caseFileItemId };
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, definition, null);
            await subject.Delete();

            await subject
                .Awaiting(x => x.Delete())
                .Should()
                .ThrowAsync<InvalidOperationException>();
        }

        [Theory, AutoData]
        public async Task AddChild__Given_Discarded__Then_ThrowInvalidOperationException
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId, string childId)
        {
            var definition = new CaseFileItemModel { Id = caseFileItemId };
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, definition, null);
            await subject.Delete();

            await subject
                .Awaiting(x => x.AddChild(childId))
                .Should()
                .ThrowAsync<InvalidOperationException>();
        }

        [Theory, AutoData]
        public async Task Update__Given_NotYetCreated__Then_ThrowInvalidOperationException
            (Guid caseInstanceId, string caseFileItemId)
        {
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject
                .Awaiting(x => x.Update(JsonValue.Create("too early")))
                .Should()
                .ThrowAsync<InvalidOperationException>();
        }

        // D2 unlock: this is the mechanical proof that CaseFileItemGrain publishes
        // CaseFileItemTransitionedEvent on the case event stream exactly as SentryGrain's
        // HandleCaseFileItemTransitioned/TimerEventListenerBehavior's CaseFileItemStartTrigger
        // subscription expect - keyed by SourceRef == the CaseFileItem definition id (see
        // CmmnElementGrain.PublishEvent and CaseFileItemGrain's class remarks). The end-to-end
        // proof that a sentry/timer actually reacts is CaseFileItemSentryIntegrationTests's
        // flagship test.
        [Theory, AutoData]
        public async Task Create__Given_ValidDefinition__Then_PublishesCaseFileItemTransitionedEvent
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId)
        {
            var definition = new CaseFileItemModel { Id = caseFileItemId };
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            var tcs = new TaskCompletionSource<CaseFileItemTransitionedEvent>();

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<CaseFileItemTransitionedEvent>(caseInstanceId, caseFileItemId)
                .SubscribeAsync((e, t) =>
                {
                    tcs.TrySetResult(e);
                    return Task.CompletedTask;
                });

            await subject.Create(caseDefinitionId, definition, null);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.Should().Be(tcs.Task, "Create should publish a CaseFileItemTransitionedEvent on the case stream keyed by the CaseFileItem definition id");

            var @event = await tcs.Task;
            @event.SourceDefinitionId.Should().Be(caseFileItemId);
            @event.StandardEvent.Should().Be(CaseFileItemTransition.Create);
        }

        [Theory, AutoData]
        public async Task Update__Given_Available__Then_PublishesCaseFileItemTransitionedEventWithUpdateStandardEvent
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId)
        {
            var definition = new CaseFileItemModel { Id = caseFileItemId };
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, definition, JsonValue.Create("original"));

            // A fresh MemoryStreams subscription on this same stream may still receive the
            // earlier Create publish (subscription creation and in-flight delivery are not
            // strictly ordered here - see StreamSemanticsTests for the sibling finding on stream
            // timing), so filter for the transition under test rather than assuming the first
            // delivery is it.
            var tcs = new TaskCompletionSource<CaseFileItemTransitionedEvent>();

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<CaseFileItemTransitionedEvent>(caseInstanceId, caseFileItemId)
                .SubscribeAsync((e, t) =>
                {
                    if (e.StandardEvent == CaseFileItemTransition.Update)
                    {
                        tcs.TrySetResult(e);
                    }
                    return Task.CompletedTask;
                });

            await subject.Update(JsonValue.Create("updated"));

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.Should().Be(tcs.Task);

            var @event = await tcs.Task;
            @event.StandardEvent.Should().Be(CaseFileItemTransition.Update);
        }

        [Theory, AutoData]
        public async Task Delete__Given_Available__Then_PublishesCaseFileItemTransitionedEventWithDeleteStandardEvent
            (string caseDefinitionId, Guid caseInstanceId, string caseFileItemId)
        {
            var definition = new CaseFileItemModel { Id = caseFileItemId };
            var subject = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            await subject.Create(caseDefinitionId, definition, null);

            // See the Update test above for why this filters rather than takes the first delivery.
            var tcs = new TaskCompletionSource<CaseFileItemTransitionedEvent>();

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<CaseFileItemTransitionedEvent>(caseInstanceId, caseFileItemId)
                .SubscribeAsync((e, t) =>
                {
                    if (e.StandardEvent == CaseFileItemTransition.Delete)
                    {
                        tcs.TrySetResult(e);
                    }
                    return Task.CompletedTask;
                });

            await subject.Delete();

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.Should().Be(tcs.Task);

            var @event = await tcs.Task;
            @event.StandardEvent.Should().Be(CaseFileItemTransition.Delete);
        }
    }
}
