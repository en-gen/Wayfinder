using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Plan.Case;
using Flow.Grains.Plan.Case.Events;
using Flow.Grains.Plan.CaseFileItem.Events;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Orleans;
using Xunit;
using CaseFileItemModel = Flow.Grains.Interfaces.Model.CaseFileItem;
using CaseModel = Flow.Grains.Interfaces.Model.Case;
using PlanItemState = Flow.Grains.Interfaces.Model.PlanItemState;
using PlanItemTransition = Flow.Grains.Interfaces.Model.PlanItemTransition;
using StageModel = Flow.Grains.Interfaces.Model.Stage;

namespace Flow.Grains.Tests.Integration.Events
{
    // ADO #59 - end-to-end proof that CmmnElementGrain.RaiseEvent's shadowed stamping (Events.
    // ActorStamping, applied from CaseRequestContext) actually reaches a real, journaled event
    // through the real RaiseEvent/ConfirmEvents path on a real TestCluster grain - not just the
    // stamping helper in isolation. Reads back via GetJournaledEvents() (the minimal seam added to
    // ICmmnElementGrain<> for exactly this - see its remarks), covering both event families this
    // work item touched: BaseCreated/BaseUpdate-derived (Case) and the CaseFileItem events that
    // implement IActorStampedEvent directly (no shared base - see IActorStampedEvent's remarks).
    [Collection(ClusterCollection.Name)]
    public class ActorStampingIntegrationTests
    {
        private const string Scope = "CPM";

        private readonly IClusterClient _clusterClient;

        public ActorStampingIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;
        }

        [Fact]
        public async Task Create__Given_AuthenticatedUser__Then_CaseCreatedIsStampedWithActor()
        {
            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            CaseRequestContext.TenantId = tenantId;
            CaseRequestContext.UserId = userId;
            CaseRequestContext.ActorPrincipalType = ActorPrincipalType.User;
            CaseRequestContext.ActorOnBehalfOf = null;

            var caseGrain = await CreateCase(tenantId);

            var events = await caseGrain.GetJournaledEvents();
            var caseCreatedEvents = events.OfType<CaseCreated>().ToList();

            caseCreatedEvents.Should().HaveCount(1);
            var caseCreated = caseCreatedEvents.Single();

            caseCreated.ActorPrincipalId.Should().Be(userId);
            caseCreated.ActorPrincipalType.Should().Be(ActorPrincipalType.User);
            caseCreated.ActorOnBehalfOf.Should().BeNull();
        }

        [Fact]
        public async Task Trigger__Given_AuthenticatedUser__Then_TransitionedIsStampedWithActor()
        {
            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            CaseRequestContext.TenantId = tenantId;
            CaseRequestContext.UserId = userId;

            var caseGrain = await CreateCase(tenantId);

            await caseGrain.Trigger(PlanItemTransition.Complete);

            var events = await caseGrain.GetJournaledEvents();
            var transitioned = events.OfType<Transitioned>()
                .FirstOrDefault(e => e.Destination == PlanItemState.Completed);

            transitioned.Should().NotBeNull("Trigger(Complete) must journal a Transitioned event to Completed");
            transitioned.ActorPrincipalId.Should().Be(userId);
            transitioned.ActorPrincipalType.Should().Be(ActorPrincipalType.User);
            transitioned.ActorOnBehalfOf.Should().BeNull();
        }

        [Fact]
        public async Task Update__Given_AuthenticatedUser__Then_ValueChangedIsStampedWithActor()
        {
            var userId = Guid.NewGuid();
            CaseRequestContext.TenantId = Guid.NewGuid();
            CaseRequestContext.UserId = userId;

            var caseFileItem = _clusterClient.GetCaseFileItem(Guid.NewGuid(), $"cfi-{Guid.NewGuid()}");
            await caseFileItem.Create("case-def", new CaseFileItemModel { Id = "cfi" }, JsonValue.Create("initial"));

            await caseFileItem.Update(JsonValue.Create("updated"));

            var events = await caseFileItem.GetJournaledEvents();
            var valueChanged = events.OfType<ValueChanged>()
                .FirstOrDefault(e => e.Value.ToJsonString() == "\"updated\"");

            valueChanged.Should().NotBeNull("Update must journal a ValueChanged event carrying the new value");
            valueChanged.ActorPrincipalId.Should().Be(userId);
            valueChanged.ActorPrincipalType.Should().Be(ActorPrincipalType.User);
            valueChanged.ActorOnBehalfOf.Should().BeNull();
        }

        [Fact]
        public async Task Update__Given_ClientPrincipalOnBehalfOfUser__Then_ActorReflectsClientAndOnBehalfOf()
        {
            CaseRequestContext.TenantId = Guid.NewGuid();
            var clientPrincipalId = Guid.NewGuid();
            CaseRequestContext.UserId = clientPrincipalId;
            CaseRequestContext.ActorPrincipalType = ActorPrincipalType.Client;
            CaseRequestContext.ActorOnBehalfOf = "end-user@example.com";

            try
            {
                var caseFileItem = _clusterClient.GetCaseFileItem(Guid.NewGuid(), $"cfi-{Guid.NewGuid()}");
                await caseFileItem.Create("case-def", new CaseFileItemModel { Id = "cfi" }, JsonValue.Create("initial"));

                var events = await caseFileItem.GetJournaledEvents();

                // CaseFileItemGrain.Create raises two events - CmmnElementDefined<CaseFileItem> :
                // BaseCreated (via base.Define) and ValueChanged (the standalone, non-BaseUpdate
                // CaseFileItem event type) - assert both actually implement IActorStampedEvent and
                // both got the same stamped actor, proving the interface-based approach covers
                // BOTH event families uniformly, not just the BaseCreated/BaseUpdate hierarchy.
                var stamped = events.OfType<IActorStampedEvent>().ToList();
                stamped.Should().HaveCount(2);

                foreach (var actor in stamped)
                {
                    actor.ActorPrincipalId.Should().Be(clientPrincipalId);
                    actor.ActorPrincipalType.Should().Be(ActorPrincipalType.Client);
                    actor.ActorOnBehalfOf.Should().Be("end-user@example.com");
                }
            }
            finally
            {
                // Restore the default identity this collection's other tests assume.
                CaseRequestContext.ActorPrincipalType = ActorPrincipalType.User;
                CaseRequestContext.ActorOnBehalfOf = null;
            }
        }

        private async Task<ICaseGrain> CreateCase(Guid tenantId)
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{Guid.NewGuid()}";

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new Flow.Grains.Interfaces.Model.CaseRoles(),
                CasePlanModel = new StageModel { Id = Scope }
            };

            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(tenantId, caseDefinitionId)
                .Define(@case);

            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope);
            await caseGrain.Create(caseDefinitionId);
            await caseGrain.Trigger(PlanItemTransition.Create);

            return caseGrain;
        }
    }
}
