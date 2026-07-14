using System;
using System.Threading.Tasks;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.Case;
using Flow.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Orleans;
using Xunit;
using CaseModel = Flow.Grains.Interfaces.Model.Case;

namespace Flow.Grains.Tests.Integration.Plan.CasePlanModel
{
    // ADO #33 - a case grain is keyed by a bare (caseInstanceId, "CPM"), with no tenant in the
    // key. CaseGrain.Create stamps the owning tenant (CaseRequestContext.TenantId at creation
    // time) onto CaseCreated/CaseStore.TenantId; CaseGrain.Trigger/GetSnapshot enforce it,
    // throwing CrossTenantAccessException for an existing case whose owning tenant does not match
    // the caller's CaseRequestContext.TenantId. This closes the hole at the grain layer (defense
    // in depth) ahead of a real HTTP auth boundary - a later app-layer sub-unit maps
    // CrossTenantAccessException to HTTP 404, matching the 404 GetCaseQueryHandler already
    // returns for a never-created case's null-Definition snapshot, so a foreign-tenant existing
    // case and a never-created case stay indistinguishable to the caller.
    [Collection(ClusterCollection.Name)]
    public class CaseTenantIsolationIntegrationTests
    {
        private const string Scope = "CPM";

        private static readonly Guid TenantA = Guid.Parse("10000000-0000-0000-0000-000000000000");
        private static readonly Guid TenantB = Guid.Parse("20000000-0000-0000-0000-000000000000");
        private static readonly Guid DefaultUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        private readonly IClusterClient _clusterClient;

        public CaseTenantIsolationIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = TenantA;
            CaseRequestContext.UserId = DefaultUserId;
        }

        [Fact]
        public async Task Case__Given_OwningTenant__Then_GetSnapshotAndTriggerSucceed()
        {
            var caseGrain = await CreateCaseUnderTenantA();

            // Still under TenantA (the owning tenant) - both public-surface reads must succeed.
            var snapshot = await caseGrain.GetSnapshot();
            snapshot.Definition.Should().NotBeNull();
            snapshot.PlanItemState.Should().Be(PlanItemState.Active);

            var triggered = await caseGrain.Trigger(PlanItemTransition.Complete);
            triggered.PlanItemState.Should().Be(PlanItemState.Completed,
                "the owning tenant must be able to drive the case exactly as before this change");
        }

        [Fact]
        public async Task Case__Given_ForeignTenant__Then_GetSnapshotThrowsCrossTenantAccessException()
        {
            var caseGrain = await CreateCaseUnderTenantA();

            CaseRequestContext.TenantId = TenantB;

            await Assert.ThrowsAsync<CrossTenantAccessException>(
                () => caseGrain.GetSnapshot());
        }

        [Fact]
        public async Task Case__Given_ForeignTenant__Then_TriggerThrowsCrossTenantAccessException()
        {
            var caseGrain = await CreateCaseUnderTenantA();

            CaseRequestContext.TenantId = TenantB;

            await Assert.ThrowsAsync<CrossTenantAccessException>(
                () => caseGrain.Trigger(PlanItemTransition.Complete));
        }

        // The cross-tenant guard must run BEFORE the Closed-state guard (CaseGrain.Trigger): a
        // foreign caller closing in on a Closed case must see the same CrossTenantAccessException
        // as any other foreign call, never the InvalidOperationException that would leak the fact
        // the case is Closed.
        [Fact]
        public async Task Case__Given_ForeignTenantAndClosedCase__Then_TriggerThrowsCrossTenantAccessExceptionNotClosedError()
        {
            var caseGrain = await CreateCaseUnderTenantA();

            var completed = await caseGrain.Trigger(PlanItemTransition.Complete);
            completed.PlanItemState.Should().Be(PlanItemState.Completed);
            var closed = await caseGrain.Trigger(PlanItemTransition.Close);
            closed.PlanItemState.Should().Be(PlanItemState.Closed);

            CaseRequestContext.TenantId = TenantB;

            await Assert.ThrowsAsync<CrossTenantAccessException>(
                () => caseGrain.Trigger(PlanItemTransition.Reactivate));
        }

        // A case that was never Create()'d must keep answering with a null-Definition snapshot
        // (the existing not-found signal - see GetCaseQueryHandler) regardless of which tenant
        // asks, since CaseStore.Defined is false and the cross-tenant guard is only meaningful
        // once a case actually exists.
        [Fact]
        public async Task Case__Given_NeverCreated__Then_GetSnapshotReturnsNullDefinitionRegardlessOfTenant()
        {
            var caseInstanceId = Guid.NewGuid();

            CaseRequestContext.TenantId = TenantB;
            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope);

            var snapshot = await caseGrain.GetSnapshot();

            snapshot.Definition.Should().BeNull(
                "a never-created case must not throw CrossTenantAccessException under any tenant - it must keep surfacing the same null-Definition not-found signal");
        }

        private async Task<ICaseGrain> CreateCaseUnderTenantA()
        {
            CaseRequestContext.TenantId = TenantA;

            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage { Id = Scope }
            };

            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .Define(@case);

            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope);
            await caseGrain.Create(caseDefinitionId);

            var created = await caseGrain.Trigger(PlanItemTransition.Create);
            created.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.6 create: the outermost Stage instance transitions directly to Active");

            return caseGrain;
        }
    }
}
