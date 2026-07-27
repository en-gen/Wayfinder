using System;
using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Infrastructure.Extensions;
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
    // Issue #161 - StageBehavior.HandleChildRepeated has no redelivery guard. Orleans streams
    // are at-least-once: SentryGrain already has a well-built guard for exactly this
    // (OccurrenceToken / SentryStore.IsRedelivery - see SentryRepetitionResetIntegrationTests),
    // but the stage repetition path has no equivalent, so a redelivered
    // PlanItemRepetitionCriteriaMetEvent spawns a SECOND physical child at the same repetition
    // index.
    //
    // Reproduction: rather than trying to force Orleans's stream provider to genuinely redeliver
    // (which would be indirect and non-deterministic), this simulates redelivery the same way
    // SentryGrainTests simulates upstream events - by publishing an identical
    // PlanItemRepetitionCriteriaMetEvent directly to the same case-event stream the real
    // repetition flow already used (same SourceScope/SourceDefinitionId/PlanItemInstanceId/
    // CurrentRepetition the production Publish call would have carried), which is exactly what a
    // stream redelivery looks like to the subscriber - it cannot tell a genuine redelivery from a
    // second identical publish.
    //
    // Positive synchronization, not a permissive sleep (#174): empirically (probed while
    // developing this suite), awaiting the test-side publish below does NOT reliably wait for the
    // subscriber's handler to run, let alone finish - the memory stream provider's producer-side
    // OnNextAsync was observed returning in single-digit milliseconds regardless of how long the
    // subscriber actually took. The immediate post-publish snapshot is therefore only a cheap
    // first check, not a synchronization point on its own. The REAL guarantee here is the active
    // poll immediately below: it re-checks on a tight cadence for a full 1s budget (short because
    // this runs on the shared, deliberately-serialized cluster - see #154/#153) and fails the
    // INSTANT a duplicate would appear, rather than sleeping once and checking only at the end
    // (the #174 failure shape).
    [Collection(ClusterCollection.Name)]
    public class RepetitionRedeliveryIntegrationTests
    {
        private const string Scope = "CPM";
        private const string TaskDefinitionId = "TaskA";
        private const string TaskPlanItemId = "PlanItemTask";

        private readonly IClusterClient _clusterClient;

        public RepetitionRedeliveryIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        [Fact]
        public async Task HandleChildRepeated__Given_SameRepetitionCriteriaMetEventDeliveredTwice__Then_ExactlyOneChildIsCreated()
        {
            var caseInstanceId = Guid.NewGuid();
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
                            ItemControl = new PlanItemControl { RepetitionRule = Rules.IsRepeatableRule }
                        }
                    }
                }
            };

            await _clusterClient.GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId).Define(@case);

            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope);
            await caseGrain.Create(caseDefinitionId);
            await caseGrain.Trigger(PlanItemTransition.Create);

            var initialSnapshot = await caseGrain.GetSnapshot();
            var rep0InstanceId = initialSnapshot.BehaviorExtension.Children[TaskPlanItemId]
                .Single(kvp => kvp.Value == 0).Key;

            var rep0Grain = _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{rep0InstanceId}");
            await rep0Grain.Trigger(PlanItemTransition.ManualStart);

            // Drives the real repeat: rep0 -> Completed re-evaluates the (always-true,
            // no-entry-criteria) RepetitionRule, publishes PlanItemRepetitionCriteriaMetEvent
            // ONCE, and the parent's HandleChildRepeated creates repetition 1 for real.
            await rep0Grain.Trigger(PlanItemTransition.Complete);

            // Arrange-phase wait for the genuine repeat to land (a positive "has it happened yet"
            // poll, same shape as RepetitionOnCompletionIntegrationTests.PollUntilGrainFound - NOT
            // the #174 shape, which is a fixed wait before an ABSENCE assertion). How long this
            // takes is an unrelated (and, on this path, orthogonal to #160/#161) confirm-timing
            // detail; the redelivery guard under test only starts after this genuinely exists.
            var childrenAfterFirstDelivery = await PollUntilChildCount(caseGrain, expectedCount: 2, TimeSpan.FromSeconds(10));
            childrenAfterFirstDelivery.Should().HaveCount(2, "rep0 and rep1 must both be recorded after the genuine repeat");
            childrenAfterFirstDelivery.Values.Should().Contain(1, "the first, genuine delivery must spawn repetition 1");

            // Simulate an at-least-once stream REDELIVERY of the exact same logical repetition
            // request: identical SourceScope/SourceDefinitionId/PlanItemInstanceId/
            // CurrentRepetition as the real one just published above. A redelivered message is,
            // by definition, indistinguishable from this at the subscriber.
            var redeliveredEvent = new PlanItemRepetitionCriteriaMetEvent(
                Scope,
                rep0InstanceId,
                TaskPlanItemId,
                currentRepetition: 0);

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<PlanItemRepetitionCriteriaMetEvent>(caseInstanceId, TaskPlanItemId)
                .OnNextAsync(redeliveredEvent);

            // Cheap first check - see class remarks for why this alone is not a synchronization
            // point (the preceding publish's completion does not guarantee the subscriber ran).
            var snapshotAfterRedelivery = await caseGrain.GetSnapshot();
            AssertNoDuplicate(snapshotAfterRedelivery.BehaviorExtension.Children[TaskPlanItemId]);

            // The real assertion: actively watch for a full, real 1s budget rather than trusting
            // a single post-await check - fails the instant a duplicate would appear, rather than
            // silently tolerating one that shows up moments later (the #174 failure shape). 1s
            // (not the original 5s) because this cluster is shared and deliberately serialized
            // across the whole suite (#154/#153) - every second here is paid by every other test
            // in the collection too.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            while (DateTime.UtcNow < deadline)
            {
                var snapshot = await caseGrain.GetSnapshot();
                AssertNoDuplicate(snapshot.BehaviorExtension.Children[TaskPlanItemId]);
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }

            void AssertNoDuplicate(System.Collections.Generic.IDictionary<string, int> children)
            {
                children.Should().HaveCount(2,
                    "a redelivered PlanItemRepetitionCriteriaMetEvent must be a no-op - #161: it must not spawn a second physical child at the same repetition index");
                children.Values.Count(v => v == 1).Should().Be(1,
                    "there must be exactly one child recorded at repetition 1, never two");
            }
        }

        // Arrange-phase helper only - waits for a genuinely new child count to appear (a positive
        // condition), not for an absence. See RepetitionOnCompletionIntegrationTests.
        // PollUntilGrainFound for the same shape used elsewhere in this suite.
        private static async Task<System.Collections.Generic.IDictionary<string, int>> PollUntilChildCount(
            ICaseGrain caseGrain, int expectedCount, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            System.Collections.Generic.IDictionary<string, int> children;
            do
            {
                var snapshot = await caseGrain.GetSnapshot();
                children = snapshot.BehaviorExtension.Children.TryGetValue(TaskPlanItemId, out var instances)
                    ? instances
                    : new System.Collections.Generic.Dictionary<string, int>();

                if (children.Count >= expectedCount) return children;

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            } while (DateTime.UtcNow < deadline);

            return children;
        }
    }
}
