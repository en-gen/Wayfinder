using System;
using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.Case;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Orleans;
using Xunit;
using CaseModel = Wayfinder.Grains.Interfaces.Model.Case;

namespace Wayfinder.Grains.Tests.Integration.Plan.CasePlanModel
{
    // D8 remainder - 8.4.1 Case Instance Lifecycle (Tables 8.5/8.6): close, re-activate, and
    // Closed-state immutability, end-to-end through ICaseGrain only.
    //
    // Table 8.6 - Case instance transitions (the rows under test):
    //   complete:    Active -> Completed  "when all the required Milestone, Stage, and Task
    //                instances have reached a terminal state ... and there are no executing
    //                (Active) Stage or Task instances"
    //   terminate:   Active -> Terminated (Case worker decision)
    //   suspend:     Active -> Suspended
    //   re-activate: Completed/Terminated/Failed/Suspended -> Active
    //   close:       Completed/Terminated/Failed/Suspended -> Closed
    //
    // Table 8.5 - Closed: "Terminal state. In this state no new activity is allowed in the
    // Case." - enforced loudly at the public surface (CaseGrain.Trigger throws) on top of the
    // state machine's own refusal to leave Closed.
    [Collection(ClusterCollection.Name)]
    public class CaseLifecycleIntegrationTests
    {
        private const string Scope = "CPM";

        private readonly IClusterClient _clusterClient;

        public CaseLifecycleIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        // The acceptance flow verbatim: create -> complete -> reactivate (spec allows:
        // Completed -> Active) -> complete again -> close -> Closed is immutable.
        [Fact]
        public async Task CaseLifecycle__Given_CompletedCase__Then_ReactivatesAndClosesAndClosedIsImmutable()
        {
            var caseGrain = await CreateEmptyCase();

            var completed = await caseGrain.Trigger(PlanItemTransition.Complete);
            completed.PlanItemState.Should().Be(PlanItemState.Completed,
                "Table 8.6 complete: vacuously satisfiable for a case with no plan items");

            var reactivated = await caseGrain.Trigger(PlanItemTransition.Reactivate);
            reactivated.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.6 re-activate: Completed -> Active by Case worker or administrator decision");

            // close is NOT available from Active (Table 8.6 lists close only from
            // Completed/Terminated/Failed/Suspended) - stays the silent unhandled-trigger no-op
            var closeFromActive = await caseGrain.Trigger(PlanItemTransition.Close);
            closeFromActive.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.6 does not permit close from Active - the attempt must not move the case");

            await caseGrain.Trigger(PlanItemTransition.Complete);
            var closed = await caseGrain.Trigger(PlanItemTransition.Close);
            closed.PlanItemState.Should().Be(PlanItemState.Closed,
                "Table 8.6 close: Completed -> Closed");

            // Table 8.5 Closed: "no new activity is allowed in the Case" - every further
            // transition attempt is rejected loudly at the public surface...
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => caseGrain.Trigger(PlanItemTransition.Reactivate));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => caseGrain.Trigger(PlanItemTransition.Complete));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => caseGrain.Trigger(PlanItemTransition.Terminate));

            // ...and the case is still exactly where it was
            (await caseGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Closed);
        }

        [Fact]
        public async Task CaseLifecycle__Given_TerminatedCase__Then_ReactivatesAndCloses()
        {
            var caseGrain = await CreateEmptyCase();

            var terminated = await caseGrain.Trigger(PlanItemTransition.Terminate);
            terminated.PlanItemState.Should().Be(PlanItemState.Terminated,
                "Table 8.6 terminate: Active -> Terminated by Case worker decision");

            var reactivated = await caseGrain.Trigger(PlanItemTransition.Reactivate);
            reactivated.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.6 re-activate: Terminated -> Active");

            await caseGrain.Trigger(PlanItemTransition.Terminate);
            var closed = await caseGrain.Trigger(PlanItemTransition.Close);
            closed.PlanItemState.Should().Be(PlanItemState.Closed,
                "Table 8.6 close: Terminated -> Closed");
        }

        [Fact]
        public async Task CaseLifecycle__Given_SuspendedCase__Then_ReactivatesToActive()
        {
            var caseGrain = await CreateEmptyCase();

            var suspended = await caseGrain.Trigger(PlanItemTransition.Suspend);
            suspended.PlanItemState.Should().Be(PlanItemState.Suspended,
                "Table 8.6 suspend: Active -> Suspended");

            var reactivated = await caseGrain.Trigger(PlanItemTransition.Reactivate);
            reactivated.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.6: a suspended case returns to Active via re-activate (the case lifecycle has no separate resume transition)");
        }

        // Reactivation must not re-run the Create entry action: the case's top-level children
        // already exist, and duplicating them would corrupt the plan.
        [Fact]
        public async Task CaseLifecycle__Given_CaseWithChild__When_Reactivated__Then_ChildrenNotDuplicated()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseGrain = await CreateCaseWithTask(caseInstanceId, required: false);

            var afterCreate = await caseGrain.GetSnapshot();
            afterCreate.BehaviorExtension.Children["PlanItemTask"].Should().HaveCount(1);

            // the non-required task is merely Enabled (not Active), so Table 8.6's completion
            // criteria hold: no Active children, no required children pending
            var completed = await caseGrain.Trigger(PlanItemTransition.Complete);
            completed.PlanItemState.Should().Be(PlanItemState.Completed,
                "an Enabled non-required child neither counts as executing nor as a pending required instance (Table 8.5/8.6)");

            var reactivated = await caseGrain.Trigger(PlanItemTransition.Reactivate);
            reactivated.PlanItemState.Should().Be(PlanItemState.Active);

            reactivated.BehaviorExtension.Children["PlanItemTask"].Should().HaveCount(1,
                "re-activation returns the case to Active without re-instantiating its plan (Table 8.6 assigns re-activate no instantiation semantics)");
        }

        // Table 8.6 complete gate at the case root: a REQUIRED child not yet terminal must block
        // manual completion of the case, loudly.
        [Fact]
        public async Task CaseLifecycle__Given_RequiredChildNotTerminal__Then_CompleteThrows()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseGrain = await CreateCaseWithTask(caseInstanceId, required: true);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => caseGrain.Trigger(PlanItemTransition.Complete));

            (await caseGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active,
                "a rejected completion must leave the case exactly where it was");

            // completing the required task lifts the block; the case then auto-completes via the
            // inherited Table 8.12 Branch 1 evaluation (all children terminal, no planning table)
            var caseSnapshot = await caseGrain.GetSnapshot();
            var taskInstanceId = caseSnapshot.BehaviorExtension.Children["PlanItemTask"].Keys.Single();
            var taskGrain = _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{taskInstanceId}");

            await taskGrain.Trigger(PlanItemTransition.ManualStart);
            await taskGrain.Trigger(PlanItemTransition.Complete);

            var completed = await PollUntil(
                async () => (await caseGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed,
                TimeSpan.FromSeconds(10));

            completed.Should().BeTrue(
                "once the required child is terminal the case's automatic completion criteria are satisfied");
        }

        private async Task<ICaseGrain> CreateEmptyCase()
        {
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

        private async Task<ICaseGrain> CreateCaseWithTask(Guid caseInstanceId, bool required)
        {
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            var taskDefinition = new HumanTask { Id = "TaskA", IsBlocking = true };

            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemTask",
                DefinitionRef = taskDefinition.Id
            };

            if (required)
            {
                planItem.ItemControl = new PlanItemControl
                {
                    RequiredRule = Rules.IsRequiredRule
                };
            }

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = Scope,
                    PlanItemDefinitions = { taskDefinition },
                    PlanItems = { planItem }
                }
            };

            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .Define(@case);

            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope);
            await caseGrain.Create(caseDefinitionId);
            await caseGrain.Trigger(PlanItemTransition.Create);

            return caseGrain;
        }

        private static async Task<bool> PollUntil(Func<Task<bool>> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await condition()) return true;
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            return await condition();
        }
    }
}
