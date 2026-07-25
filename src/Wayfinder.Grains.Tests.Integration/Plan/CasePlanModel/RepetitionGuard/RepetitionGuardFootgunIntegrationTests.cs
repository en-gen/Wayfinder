using System;
using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.Case;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Orleans;
using Xunit;
using CaseModel = Wayfinder.Grains.Interfaces.Model.Case;

namespace Wayfinder.Grains.Tests.Integration.Plan.CasePlanModel.RepetitionGuard
{
    // ADO #67 - the documented #19 foot-gun, proven bounded end-to-end through real grains and
    // streams (model-driven, through ICaseGrain.Create + Trigger only - no hand-wired grains).
    //
    // Foot-gun shape (8.6.4 is spec-faithful here - see RepetitionGuardOptions' remarks for why
    // this is deliberately NOT treated as a CMMN conformance concern):
    //   Case "CPM" (outermost Stage/CasePlanModel)
    //     PlanItemDefinitions: SourceTask (HumanTask, IsBlocking = FALSE)
    //     PlanItems: PlanItemSource -> SourceTask, NO entry criteria,
    //                ItemControl.RepetitionRule = Rules.IsRepeatableRule (constant TRUE),
    //                ItemControl.ManualActivationRule = Rules.NotManuallyActivated (constant FALSE)
    //
    // Every completion cascades fully unattended: Create -> Available -> (ManualActivationRule
    // FALSE, 8.6.2) Start -> Active -> (non-blocking, Table 5.39) auto-Complete ->
    // TryRepeatOnCompleteOrTerminate re-evaluates RepetitionRule (TRUE, no entry criteria, 8.6.4)
    // -> publishes PlanItemRepetitionCriteriaMetEvent -> the CasePlanModel's HandleChildRepeated
    // spawns the next repetition - with NO human or external event ever entering the loop. Left
    // unbounded this is exactly the #19 infinite-spawn scenario; RepetitionGuardClusterFixture
    // pins RepetitionGuardOptions.MaxRepetitionsPerPlanItem to
    // RepetitionGuardClusterFixture.LowCeiling (5) so the cascade must halt there instead of
    // running away - the flagship proof that the engine-extension ceiling (#67) actually bounds
    // the spec-faithful loop.
    [Collection(RepetitionGuardClusterCollection.Name)]
    public class RepetitionGuardFootgunIntegrationTests
    {
        private const string Scope = "CPM";
        private const string TaskDefinitionId = "SourceTask";
        private const string TaskPlanItemId = "PlanItemSource";
        private const string SentinelDefinitionId = "SentinelTask";
        private const string SentinelPlanItemId = "PlanItemSentinel";

        private readonly IClusterClient _clusterClient;

        public RepetitionGuardFootgunIntegrationTests(RepetitionGuardClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        [Fact]
        public async Task CaseCreate__Given_NonBlockingTaskRepetitionRuleTrueNoEntryCriteriaAndLowCeiling__Then_SpawnsExactlyToCeilingThenFaultsCase()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            var taskDefinition = new HumanTask { Id = TaskDefinitionId, IsBlocking = false };

            // Sentinel sibling (blocking HumanTask, no ItemControl - so ManualActivationRule
            // defaults TRUE per 8.6.2/Table 5.51 and it simply sits Enabled, never triggered by
            // this test): AutoComplete defaults FALSE on a Stage/CasePlanModel (Spec.CMMN.MODEL's
            // own default), and Table 8.12's autoComplete=FALSE Branch 1 (StageBehavior.
            // HandleChildTransitioned) auto-completes the CasePlanModel the instant EVERY
            // currently-existing child is momentarily terminal - which, with PlanItemSource as
            // the ONLY child, is true again after every single repetition completes (each
            // repetition is created-and-auto-completed inside one child-grain turn, well before
            // the NEXT repetition is spawned). Without this sentinel the CasePlanModel races to
            // Completed after the very first repetition, before the ceiling can ever be
            // exercised - the sentinel's permanently-Enabled (non-terminal) presence is what
            // keeps the CasePlanModel Active long enough for RepetitionGuardOptions.
            // MaxRepetitionsPerPlanItem to be the thing that actually ends the case, proving the
            // ceiling (not an unrelated auto-complete race) is what halted the cascade.
            var sentinelDefinition = new HumanTask { Id = SentinelDefinitionId, IsBlocking = true };

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = Scope,
                    PlanItemDefinitions = { taskDefinition, sentinelDefinition },
                    PlanItems =
                    {
                        new Interfaces.Model.PlanItem
                        {
                            Id = TaskPlanItemId,
                            DefinitionRef = taskDefinition.Id,
                            ItemControl = new PlanItemControl
                            {
                                RepetitionRule = Rules.IsRepeatableRule,
                                ManualActivationRule = Rules.NotManuallyActivated
                            }
                        },
                        new Interfaces.Model.PlanItem
                        {
                            Id = SentinelPlanItemId,
                            DefinitionRef = sentinelDefinition.Id
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

            // The cascade (each repetition's own auto-Complete, PLUS the parent's stream-delivered
            // HandleChildRepeated spawning the next one) runs across several separate grain turns,
            // not synchronously inside the Trigger(Create) call above - poll until it settles into
            // the terminal Failed state the ceiling breach drives it to.
            var caseSnapshot = await PollUntilCaseState(caseInstanceId, PlanItemState.Failed, TimeSpan.FromSeconds(15));

            caseSnapshot.PlanItemState.Should().Be(PlanItemState.Failed,
                "ADO #67: breaching RepetitionGuardOptions.MaxRepetitionsPerPlanItem must fault the containing CasePlanModel instead of spawning past the ceiling - an unbounded spawn would never settle here");

            var instances = caseSnapshot.BehaviorExtension.Children[TaskPlanItemId];
            instances.Should().HaveCount(RepetitionGuardClusterFixture.LowCeiling,
                "exactly the ceiling's worth of repetitions (indices 0..ceiling-1) must be spawned - not one more, not fewer");
            instances.Values.Should().BeEquivalentTo(Enumerable.Range(0, RepetitionGuardClusterFixture.LowCeiling),
                "the spawned repetitions must be a contiguous run starting at 0 - the (ceiling+1)th spawn must have been refused, not merely some spawn along the way");

            // Absence half: confirm the halt is durable, not a race window that a slightly longer
            // wait would show a 6th instance sneaking in.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            var settledSnapshot = await caseGrain.GetSnapshot();
            settledSnapshot.BehaviorExtension.Children[TaskPlanItemId].Should().HaveCount(RepetitionGuardClusterFixture.LowCeiling,
                "no further instance may appear after the ceiling breach faults the container");
        }

        private async Task<CaseSnapshot> PollUntilCaseState(Guid caseInstanceId, PlanItemState state, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            CaseSnapshot snapshot;
            do
            {
                snapshot = await _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope).GetSnapshot();
                if (snapshot.PlanItemState == state) return snapshot;
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            } while (DateTime.UtcNow < deadline);

            return snapshot;
        }
    }
}
