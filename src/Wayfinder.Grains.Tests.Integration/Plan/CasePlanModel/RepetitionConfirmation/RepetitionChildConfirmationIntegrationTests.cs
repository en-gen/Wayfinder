using System;
using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.Case;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Orleans;
using Orleans.Runtime;
using Xunit;
using CaseModel = Wayfinder.Grains.Interfaces.Model.Case;

namespace Wayfinder.Grains.Tests.Integration.Plan.CasePlanModel.RepetitionConfirmation
{
    // Issue #160 - StageBehavior.HandleChildRepeated -> CreateChild raises ChildRepeated/
    // ChildCreated on the PARENT (Case/Stage) host WITHOUT ever confirming them (contrast
    // HandleEnterActiveFromStart, which batches a trailing Host.ConfirmEvents() after its
    // CreateChild calls - see that method's remarks). JournaledGrain semantics: RaiseEvent
    // updates TentativeState immediately (visible for the remainder of THIS activation) but
    // is only durable across a deactivation once ConfirmEvents() has actually completed. If
    // nothing else happens to opportunistically confirm the host before it deactivates, the
    // parent's StageBehaviorStore.Children bookkeeping for that repetition is silently lost -
    // even though the repeated child's OWN grain independently confirmed its own state and is
    // still alive - because nothing else ever points back at it.
    //
    // Reproduction technique: publish PlanItemRepetitionCriteriaMetEvent directly to the Case's
    // subscription stream from test code (same shape as RepetitionRedeliveryIntegrationTests'
    // simulated redelivery), rather than driving it indirectly through rep0's own
    // Trigger(Complete) -> Stateless transition -> HandleTransitioned -> Publish cascade - the
    // extra hop of that cascade adds scheduling latency this test does not need. Runs against its
    // own dedicated TestCluster (RepetitionConfirmationClusterFixture), NOT the shared
    // ClusterFixture/ClusterCollection every other integration test reuses: this test calls
    // IManagementGrain.ForceActivationCollection(TimeSpan.Zero), which deactivates every
    // activation in the ENTIRE cluster it targets. On the shared cluster, that forced sweep - and
    // unrelated background grain/stream activity from other tests contending for the same
    // in-process scheduler - made the specific race this test targets (does the parent's
    // ConfirmEvents complete before the forced deactivation lands) non-deterministic in EITHER
    // direction, confirmed empirically while developing this test (reliable alone, flaky under
    // the full suite's load). An otherwise-idle dedicated cluster removes that contention,
    // matching RepetitionGuardClusterFixture's established precedent for the same class of
    // problem.
    [Collection(RepetitionConfirmationClusterCollection.Name)]
    public class RepetitionChildConfirmationIntegrationTests
    {
        private const string Scope = "CPM";
        private const string TaskDefinitionId = "TaskA";
        private const string TaskPlanItemId = "PlanItemTask";

        private readonly IClusterClient _clusterClient;

        public RepetitionChildConfirmationIntegrationTests(RepetitionConfirmationClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        [Fact]
        public async Task HandleChildRepeated__Given_HostDeactivatesImmediatelyAfterRepetition__Then_ChildBookkeepingSurvivesReactivation()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseGrain = await CreateCase(caseInstanceId);

            var initialSnapshot = await caseGrain.GetSnapshot();
            var rep0InstanceId = initialSnapshot.BehaviorExtension.Children[TaskPlanItemId]
                .Single(kvp => kvp.Value == 0).Key;

            var repetitionEvent = new PlanItemRepetitionCriteriaMetEvent(
                Scope,
                rep0InstanceId,
                TaskPlanItemId,
                currentRepetition: 0);

            // Single-hop, directly-awaited publish. Empirically (see DelayedInMemoryGrainStorage's
            // remarks), awaiting this does NOT wait for the subscriber's handler to run, let
            // alone finish - the memory stream provider's producer-side OnNextAsync returns in a
            // handful of milliseconds regardless. That is exactly why this fixture wires up
            // DelayedInMemoryGrainStorage for "Default": it stretches the one operation that DOES
            // matter (the actual grain-state write) out to several seconds, so the wait below can
            // reliably straddle it without needing to guess at Orleans's internal scheduling.
            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<PlanItemRepetitionCriteriaMetEvent>(caseInstanceId, TaskPlanItemId)
                .OnNextAsync(repetitionEvent);

            // Wait comfortably past DelayedInMemoryGrainStorage.WriteDelay before forcing
            // collection - NOT to let anything "settle" opportunistically, but because
            // ForceActivationCollection(TimeSpan.Zero) was found, empirically, to tear down an
            // activation that is still BUSY mid-call (not just idle ones) - so forcing collection
            // while the fixed handler's own Host.ConfirmEvents() is still in flight would destroy
            // its own in-progress write and fail this test for the wrong reason on FIXED code too.
            // Waiting past the write delay first guarantees the fixed handler has already
            // returned (confirmed, idle) by the time collection is forced - the fair comparison
            // point: does an ALREADY-FINISHED handler's work survive deactivation, which is
            // exactly #160's question. On unpatched code there is no ConfirmEvents call at all, so
            // the handler already returned within milliseconds regardless - forcing collection
            // here still lands on an idle activation whose ChildRepeated/ChildCreated were never
            // durably written.
            await Task.Delay(DelayedInMemoryGrainStorage.WriteDelay + TimeSpan.FromSeconds(1));

            await _clusterClient.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

            var caseSnapshotAfter = await caseGrain.GetSnapshot();

            caseSnapshotAfter.BehaviorExtension.Children.Should().ContainKey(TaskPlanItemId,
                "the parent's own bookkeeping for repeating children must survive its activation being torn down");

            var rep1InstanceId = caseSnapshotAfter.BehaviorExtension.Children[TaskPlanItemId]
                .Where(kvp => kvp.Value == 1)
                .Select(kvp => kvp.Key)
                .SingleOrDefault();

            rep1InstanceId.Should().NotBeNull(
                "repetition 1 of TaskA must still be recorded under the parent's Children map after " +
                "the parent's host activation deactivates and reactivates - #160: ChildRepeated/" +
                "ChildCreated must be confirmed before the handler returns, not left to an " +
                "opportunistic later confirm that a forced/idle deactivation can race ahead of");

            // Belt-and-braces: the child itself must still be a live, addressable grain with the
            // expected repetition recorded on ITS OWN (independently-confirmed) state - proving
            // this is a bookkeeping-loss bug, not merely a "child never got created" bug.
            var rep1Grain = _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{rep1InstanceId}");
            var rep1Snapshot = await rep1Grain.GetSnapshot();
            rep1Snapshot.Repetition.Should().Be(1);
        }

        private async Task<ICaseGrain> CreateCase(Guid caseInstanceId)
        {
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            var taskDefinition = new HumanTask { Id = TaskDefinitionId, IsBlocking = true };

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = Scope,
                    PlanItemDefinitions = { taskDefinition },
                    PlanItems =
                    {
                        new Interfaces.Model.PlanItem
                        {
                            Id = TaskPlanItemId,
                            DefinitionRef = taskDefinition.Id,
                            ItemControl = new PlanItemControl
                            {
                                RepetitionRule = Rules.IsRepeatableRule
                            }
                        }
                    }
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
    }
}
