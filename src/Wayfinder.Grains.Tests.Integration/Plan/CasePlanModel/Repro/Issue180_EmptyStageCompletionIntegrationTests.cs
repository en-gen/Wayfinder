using System;
using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.Case;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Orleans;
using Xunit;
using CaseModel = Wayfinder.Grains.Interfaces.Model.Case;

namespace Wayfinder.Grains.Tests.Integration.Plan.CasePlanModel.Repro
{
    // REPRODUCTION for issue #180 (P2) - NOT independently verified before this test; do not
    // treat a pass here as proof the engine is correct beyond what this specific scenario
    // exercises, and do not treat a fail as license to change engine code (repro-only branch).
    //
    // Claim: Table 8.12 completion criteria are evaluated EXCLUSIVELY from inside
    // StageBehavior.HandleChildTransitioned (src/Wayfinder.Grains/Plan/PlanItem/Behaviors/
    // StageBehavior.cs), behind `if (@event.Destination.IsTerminal())`. A Stage with zero
    // PlanItems (and no PlanningTable) produces zero children and therefore zero child
    // transitions - HandleEnterActiveFromStart's `Task.WhenAll(PlanItemDefinition.PlanItems.
    // Select(x => CreateChild(x)))` is a no-op over an empty sequence - so nothing ever
    // triggers a completion evaluation, and an autoComplete=true empty Stage (which should
    // complete vacuously: "no Active children" is trivially true, "all required children
    // terminal" is trivially true over an empty set) should stay Active forever.
    //
    // Case shape (fresh case instance):
    //   Case "CPM" (outermost Stage = CasePlanModel)
    //     PlanItemDefinitions: StageEmpty (autoComplete=true, PlanItems = [], PlanningTable = null)
    //     PlanItems: PlanItemStage -> StageEmpty
    [Collection(ClusterCollection.Name)]
    public class Issue180_EmptyStageCompletionIntegrationTests
    {
        private const string Scope = "CPM";
        private const string StageDefinitionId = "StageEmpty";
        private const string StagePlanItemId = "PlanItemStage";

        private readonly IClusterClient _clusterClient;

        public Issue180_EmptyStageCompletionIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        // Positive synchronization point: poll for Completed with a generous 30s budget (per
        // #174 - the suite's existing "must NOT fire" 500ms-sleep pattern is silently permissive
        // and is deliberately NOT reused here; this is a positive "did happen" assertion, so a
        // slow-but-correct engine has 30s to prove it while a genuinely wedged Stage will still
        // be Active at the deadline and the assertion fails honestly).
        [Fact]
        public async Task Stage__Given_AutoCompleteStageWithZeroPlanItems__Then_StageCompletesVacuously()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            var stageDefinition = new Stage
            {
                Id = StageDefinitionId,
                AutoComplete = true
                // PlanItems left empty, PlanningTable left null - the exact "zero plan items"
                // shape the issue describes.
            };

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = Scope,
                    PlanItemDefinitions = { stageDefinition },
                    PlanItems =
                    {
                        new Interfaces.Model.PlanItem
                        {
                            Id = StagePlanItemId,
                            DefinitionRef = stageDefinition.Id
                        }
                    }
                }
            };

            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .Define(@case);

            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope);
            await caseGrain.Create(caseDefinitionId);
            var caseSnapshot = await caseGrain.Trigger(PlanItemTransition.Create);

            var stageInstanceId = caseSnapshot.BehaviorExtension.Children[StagePlanItemId].Keys.Single();
            var stageGrain = _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{stageInstanceId}");

            // 8.6.2/Table 5.51: no ManualActivationRule -> default TRUE -> the stage waits
            // Enabled; ManualStart drives it Active. HandleEnterActiveFromStart's CreateChild
            // fan-out is over an EMPTY PlanItems collection, so this call creates zero children -
            // the precondition under test.
            //
            // Fix verification note (#180): PlanItemGrain.Trigger awaits the behavior's ENTIRE
            // transition cascade (Stateless chains every OnEntryFromAsync synchronously) before
            // taking the snapshot it returns, so with the fix in place a genuinely childless
            // autoComplete=TRUE Stage's vacuous completion check runs INLINE, in this same
            // Trigger(ManualStart) call - there is no separate instant where this Stage is
            // observably Active but not yet Completed, so the returned snapshot already reflects
            // Completed. (Confirmed empirically: before this fix landed, this assertion read
            // PlanItemState.Active and the test failed at the PollUntil below instead, per the
            // captured pre-fix repro output.)
            (await stageGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Enabled);
            var stageSnapshot = await stageGrain.Trigger(PlanItemTransition.ManualStart);
            stageSnapshot.PlanItemState.Should().Be(PlanItemState.Completed);

            var completed = await PollUntil(
                async () => (await stageGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed,
                TimeSpan.FromSeconds(30));

            completed.Should().BeTrue(
                "Table 8.12 autoComplete=TRUE is satisfied vacuously by a Stage with zero children - " +
                "if completion is only ever evaluated from inside HandleChildTransitioned (issue #180), " +
                "a Stage that creates no children can never trigger that evaluation and is wedged Active forever");
        }

        // Guard for the OTHER direction of the #180 fix: autoComplete=FALSE requires EXPLICIT
        // completion (Table 8.12's Manual Completion branch) - an empty, non-auto-complete
        // Stage must NOT auto-complete on its own just because it happens to have zero children.
        //
        // Deliberately NOT a sleep-then-assert-it-hasn't-happened test (see #174 - a fixed
        // window is silently permissive: it can pass merely because nothing had run yet, proving
        // nothing about whether it ever WOULD run). No timing window is needed here at all: the
        // fix (StageBehavior.HandleEnterActiveFromStart) gates its new eager completion check on
        // `PlanItemDefinition.AutoComplete` synchronously, before anything async happens - for an
        // autoComplete=FALSE Stage that check is skipped in the very same call, so the state
        // Trigger(ManualStart) returns is deterministically still Active, not a race to be won.
        // The real proof this isn't secretly wedged in some NEW bad way instead is a positive
        // synchronization point: an explicit manual Trigger(Complete) immediately afterward must
        // still succeed (Table 8.12's Manual Completion branch - `requiredChildrenTerminal` over
        // an empty child set - is vacuously satisfied too), proving the Stage is legitimately
        // open for completion, it just does not complete itself.
        [Fact]
        public async Task Stage__Given_NotAutoCompleteStageWithZeroPlanItems__Then_StageDoesNotAutoCompleteButManualSucceeds()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            var stageDefinition = new Stage
            {
                Id = StageDefinitionId,
                AutoComplete = false
                // PlanItems left empty, PlanningTable left null - same "zero plan items" shape
                // as the autoComplete=TRUE test above, the other side of the branch.
            };

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = Scope,
                    PlanItemDefinitions = { stageDefinition },
                    PlanItems =
                    {
                        new Interfaces.Model.PlanItem
                        {
                            Id = StagePlanItemId,
                            DefinitionRef = stageDefinition.Id
                        }
                    }
                }
            };

            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .Define(@case);

            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope);
            await caseGrain.Create(caseDefinitionId);
            var caseSnapshot = await caseGrain.Trigger(PlanItemTransition.Create);

            var stageInstanceId = caseSnapshot.BehaviorExtension.Children[StagePlanItemId].Keys.Single();
            var stageGrain = _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{stageInstanceId}");

            (await stageGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Enabled);
            var stageSnapshot = await stageGrain.Trigger(PlanItemTransition.ManualStart);

            stageSnapshot.PlanItemState.Should().Be(PlanItemState.Active,
                "autoComplete=FALSE requires explicit completion (Table 8.12's Manual Completion " +
                "branch) - an empty Stage must not auto-complete just because it has zero children");

            var afterManualComplete = await stageGrain.Trigger(PlanItemTransition.Complete);

            afterManualComplete.PlanItemState.Should().Be(PlanItemState.Completed,
                "the Stage was never wedged - Table 8.12's Manual Completion branch is vacuously " +
                "satisfied by zero required children, so an explicit Trigger(Complete) succeeds " +
                "immediately");
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
