using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.Case;
using Wayfinder.Grains.Interfaces.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Orleans;
using Xunit;
using CaseModel = Wayfinder.Grains.Interfaces.Model.Case;
using SentryModel = Wayfinder.Grains.Interfaces.Model.Sentry;

namespace Wayfinder.Grains.Tests.Integration.Plan.CasePlanModel
{
    // Issue #178 - "zombie execution": before this fix, a Terminated Stage could still spawn a
    // repetition child.
    //
    // Root cause (two independent contributors):
    //   - BaseBehavior.HandleEnterTerminal (BaseBehavior.cs) unsubscribes ONLY ExitCriteria on
    //     entry to a terminal state:
    //         Task HandleEnterTerminal() => Task.WhenAll(Host.Definition.ExitCriteria
    //             .Select(c => Host.UnsubscribeFrom<SentrySatisfiedEvent>(c.SentryRef)));
    //     A child's EntryCriteria subscription is left standing even after the child reaches
    //     Terminated via a parent-cascaded Exit (HandleParentTransitioned) - so the event stream
    //     StageBehavior.HandleChildRepeated listens on can still deliver.
    //   - StageBehavior.HandleChildRepeated (StageBehavior.cs) used to never consult
    //     Host.State.PlanItemState - it acted on ANY PlanItemRepetitionCriteriaMetEvent scoped to
    //     this stage, regardless of whether the stage itself was Active, Terminated, or Suspended.
    //     Fixed by branching on Host.State.PlanItemState before ever reaching the ceiling-check/
    //     CreateChild machinery: Completed/Terminated/Closed refuse outright (Table 8.9 - a
    //     terminating Stage cascades exit to every non-terminal child first, so nothing live can
    //     remain to legitimately request one); Suspended buffers the request for replay on resume
    //     instead of dropping it (docs/03-cmmn-execution-semantics.md section 2 - the request was
    //     earned before suspension, merely late); Failed refuses but raises an observable event
    //     (RepetitionRefusedWhileFailed), since re-activation is a human recovery action and a
    //     silent stale replay into a just-recovered case would be surprising.
    //
    // This scenario (per the issue's own reproduction sketch) exercises the Terminated half: a
    // nested Stage S with its own exit criterion, containing a repeating child Task T whose entry
    // criterion is an INDEPENDENT Sentry (watches a sibling PlanItem inside S, not T's own
    // lifecycle). S is driven to Active, its exit criterion fires (S -> Terminated, cascading T ->
    // Terminated per Table 8.9), and only THEN is the independent sentry satisfied a SECOND time
    // (Figure 8.5 B/B' - a second, distinct completion of the sibling source, exactly the pattern
    // this suite already establishes in SentryRepetitionResetIntegrationTests/SentryScenarios). T's
    // still-live entry-criterion subscription reacts; the fix must refuse the resulting spawn
    // attempt rather than let StageBehavior.HandleChildRepeated create a new T instance inside the
    // already-Terminated S. The Suspended-buffers-and-replays-on-resume behavior, the redelivery-
    // while-suspended dedupe, and the ceiling-still-applies-on-replay guarantee are all covered at
    // the faster unit layer instead (StageBehaviorTests_HandleChildRepeated_RepetitionGuard.cs,
    // Stores/StageBehaviorStoreTests.cs) - this integration test is reserved for the one assertion
    // that genuinely needs the real grain/stream/cascade machinery: that a live parent-terminate
    // cascade actually leaves the child's entry-criterion subscription active, and the fix refuses
    // the resulting spawn attempt end-to-end.
    //
    // ASSERTION STRATEGY (issue #174 compliance - "a Terminated stage must not spawn a repetition
    // child" is fundamentally a NEGATIVE claim, so a bare fixed sleep would be silently
    // permissive):
    //   1. POSITIVE lower bound: poll (30s budget) for T's OWN Repeated flag to flip true. This is
    //      strictly on the causal path BEFORE the Stage's HandleChildRepeated ever runs - Repeated
    //      is only raised AFTER Publish(PlanItemRepetitionCriteriaMetEvent) has already been
    //      awaited (TaskBehavior.HandleSentrySatisfied: Task.WhenAll(Unsubscribe, Publish) THEN
    //      RaiseEvent(Repeated)) - so observing Repeated == true proves the very event S's
    //      HandleChildRepeated reacts to has already been published, not merely attempted.
    //   2. No clean positive UPPER bound exists for "the Stage has finished NOT reacting" - the
    //      cascade under test produces no other observable side effect besides the (refused) spawn
    //      itself. A documented 30s poll budget is used for the absence check - generous enough
    //      that the handful of in-process awaits a real CreateChild call performs (DefineRepetition,
    //      Trigger(Create), two SubscribeTo calls) would trivially complete inside it on any
    //      engine, fixed or not. This budget is a deliberate, acknowledged limitation, not a
    //      silent one.
    [Collection(ClusterCollection.Name)]
    public class RepetitionAfterTerminationIntegrationTests
    {
        private const string Scope = "CPM";

        private readonly IClusterClient _clusterClient;

        public RepetitionAfterTerminationIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        [Fact]
        public async Task TerminatedStage__Given_RepeatingChildEntryCriterionSatisfiedAfterExit__Then_NoNewChildIsSpawned()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            const string stageSDefinitionId = "StageS";
            const string stageSPlanItemId = "PlanItemStageS";
            const string exitSentryId = "ExitSentry";
            const string exitItemId = "ExitItem";

            const string independentSourceTaskDefinitionId = "IndependentSourceTask";
            const string independentSourcePlanItemId = "IndependentSourcePlanItem";
            const string repeatSentryId = "RepeatSentry";

            const string repeatingTaskDefinitionId = "RepeatingTask";
            const string repeatingPlanItemId = "RepeatingPlanItem";

            // S's own exit criterion - declared at CPM level (the containing Stage/PlanFragment
            // of PlanItemStageS, per 5.4.5.1), exactly the Sentry_ExitCriterionStage.cmmn shape
            // this suite already establishes (SentryScenarios.Sentry__Given_StageExitCriterion).
            var exitSentryDefinition = new SentryModel
            {
                Id = exitSentryId,
                OnParts = { new CaseFileItemOnPart { SourceRef = exitItemId, StandardEvent = CaseFileItemTransition.Update } }
            };

            // T's entry criterion - declared WITHIN S (5.4.5.1: a criterion's Sentry must be
            // contained by the Stage/PlanFragment that contains the referencing PlanItem), but
            // its OnPart watches a SIBLING PlanItem also living inside S - "independent" of T's
            // own lifecycle, per the issue's reproduction sketch.
            var repeatSentryDefinition = new SentryModel
            {
                Id = repeatSentryId,
                OnParts = { new PlanItemOnPart { SourceRef = independentSourcePlanItemId, StandardEvent = PlanItemTransition.Complete } }
            };

            var independentSourceTaskDefinition = new HumanTask { Id = independentSourceTaskDefinitionId, IsBlocking = true };
            var repeatingTaskDefinition = new HumanTask { Id = repeatingTaskDefinitionId, IsBlocking = true };

            var independentSourcePlanItem = new Interfaces.Model.PlanItem
            {
                Id = independentSourcePlanItemId,
                DefinitionRef = independentSourceTaskDefinition.Id
            };

            var repeatingPlanItem = new Interfaces.Model.PlanItem
            {
                Id = repeatingPlanItemId,
                DefinitionRef = repeatingTaskDefinition.Id,
                EntryCriteria = { new EntryCriterion { SentryRef = repeatSentryId } },
                ItemControl = new PlanItemControl
                {
                    RepetitionRule = Rules.IsRepeatableRule,
                    ManualActivationRule = Rules.NotManuallyActivated
                }
            };

            var stageSDefinition = new Stage
            {
                Id = stageSDefinitionId,
                AutoComplete = false,
                Sentries = { repeatSentryDefinition },
                PlanItems = { independentSourcePlanItem, repeatingPlanItem }
            };

            var stageSPlanItem = new Interfaces.Model.PlanItem
            {
                Id = stageSPlanItemId,
                DefinitionRef = stageSDefinition.Id,
                ExitCriteria = { new ExitCriterion { SentryRef = exitSentryId } }
            };

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = Scope,
                    AutoComplete = false,
                    Sentries = { exitSentryDefinition },
                    // Both task definitions hoisted to the casePlanModel level - the established
                    // convention this suite's own nested-stage samples use
                    // (Sentry_ExitCriterionStage.cmmn's remarks), independent of whether nested
                    // declarations resolve today.
                    PlanItemDefinitions = { independentSourceTaskDefinition, repeatingTaskDefinition, stageSDefinition },
                    PlanItems = { stageSPlanItem }
                }
            };

            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .Define(@case);

            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope);
            await caseGrain.Create(caseDefinitionId);
            await caseGrain.Trigger(PlanItemTransition.Create);

            var stageSGrain = await PollUntilGrainFound(caseInstanceId, Scope, stageSPlanItemId, 0, TimeSpan.FromSeconds(30));
            stageSGrain.Should().NotBeNull("StageS must have been instantiated by Case creation");

            (await stageSGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Enabled,
                "StageS declares no ManualActivationRule (Table 5.51 default TRUE) - it waits Enabled for a manual start");

            var stageSActive = await stageSGrain.Trigger(PlanItemTransition.ManualStart);
            stageSActive.PlanItemState.Should().Be(PlanItemState.Active, "manually starting StageS must instantiate its planned children (8.7)");

            // StageS's own address (needed to construct its children's grain keys below) is
            // resolved from the CasePlanModel's OWN bookkeeping (StageS is a direct child of
            // CPM) - distinct from stageSActive.BehaviorExtension, which is StageS's OWN
            // children snapshot (independentSourcePlanItem/repeatingPlanItem), not CPM's.
            var stageSInstanceId = await ResolveTopLevelInstanceId(caseInstanceId, stageSPlanItemId, 0);
            var stageSAddress = $"{Scope}.{stageSInstanceId}";
            var stageSChildren = (StageBehaviorSnapshot)stageSActive.BehaviorExtension;

            var sourceRep0Grain = _clusterClient.GetGrain<IPlanItemInternalGrain>(
                caseInstanceId, $"{stageSAddress}.{ResolveInstanceId(stageSChildren, independentSourcePlanItemId, 0)}");
            var repeatingRep0Grain = _clusterClient.GetGrain<IPlanItemInternalGrain>(
                caseInstanceId, $"{stageSAddress}.{ResolveInstanceId(stageSChildren, repeatingPlanItemId, 0)}");

            (await repeatingRep0Grain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available,
                "T must be waiting on RepeatSentry before either source completion, or this test would prove nothing");

            // B: the model-instantiated independent source completes -> RepeatSentry satisfied
            // for the first time -> T (Available, MAR FALSE) moves directly to Active.
            await sourceRep0Grain.Trigger(PlanItemTransition.ManualStart);
            await sourceRep0Grain.Trigger(PlanItemTransition.Complete);

            var tActivated = await PollUntil(
                async () => (await repeatingRep0Grain.GetSnapshot()).PlanItemState == PlanItemState.Active,
                TimeSpan.FromSeconds(30));
            tActivated.Should().BeTrue("the first independent-source completion should satisfy RepeatSentry and activate T directly (MAR is FALSE)");

            // Fire StageS's OWN exit criterion via a case-file update - no Case-worker trigger,
            // exactly Sentry_ExitCriterionStage.cmmn's shape (SentryScenarios, already pinned
            // green in this suite).
            var exitItem = _clusterClient.GetCaseFileItem(caseInstanceId, exitItemId);
            await exitItem.Create(caseDefinitionId, new Interfaces.Model.CaseFileItem { Id = exitItemId }, JsonNode.Parse("""{"abort": false}"""));
            await exitItem.Update(JsonNode.Parse("""{"abort": true}"""));

            var stageSTerminated = await PollUntil(
                async () => (await stageSGrain.GetSnapshot()).PlanItemState == PlanItemState.Terminated,
                TimeSpan.FromSeconds(30));
            stageSTerminated.Should().BeTrue("StageS's own exit criterion must terminate it (Table 8.8 exit) before this test can probe the zombie-spawn claim");

            var tTerminated = await PollUntil(
                async () => (await repeatingRep0Grain.GetSnapshot()).PlanItemState == PlanItemState.Terminated,
                TimeSpan.FromSeconds(30));
            tTerminated.Should().BeTrue("Table 8.9: StageS terminating must cascade Exit to its Active child T (already pinned by KnownGapScenarios.StageExit__...)");

            (await repeatingRep0Grain.GetSnapshot()).Repeated.Should().BeFalse(
                "T reached Terminated via the PARENT-cascaded Exit, not by detecting a repeat on its own entry criterion - Repeated must still be false here, or the scenario below would prove nothing new");

            // B': a second, distinct instance of the independent source completes - AFTER StageS
            // (and T) are already Terminated. Table 5.30/D5: a distinct completion (different
            // grain instance sharing the same PlanItem.Id) genuinely re-arms RepeatSentry.
            var sourceRep1Grain = _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{stageSAddress}.ZombieProbeSourceB2");
            await sourceRep1Grain.Define(caseDefinitionId, independentSourcePlanItem);
            await sourceRep1Grain.Trigger(PlanItemTransition.Create);
            await sourceRep1Grain.Trigger(PlanItemTransition.ManualStart);
            await sourceRep1Grain.Trigger(PlanItemTransition.Complete);

            // Synchronization anchor #1 (positive lower bound - see class remarks): T's own
            // Repeated flag flipping true PROVES PlanItemRepetitionCriteriaMetEvent has already
            // been published - i.e. that StageS's HandleChildRepeated has, at minimum, already
            // been triggered to run (a Terminated T taking the repetition branch on this second,
            // genuine satisfaction despite never having been un-terminated is itself already part
            // of the claimed defect).
            var tRepeatedAfterTermination = await PollUntil(
                async () => (await repeatingRep0Grain.GetSnapshot()).Repeated,
                TimeSpan.FromSeconds(30));

            // Synchronization anchor #2 (documented 30s absence budget - see class remarks: no
            // cleaner positive upper bound exists for "the cascade has finished NOT spawning").
            var secondChildSpawned = await PollUntil(
                async () =>
                {
                    var stageSSnapshot = await stageSGrain.GetSnapshot();
                    var children = (StageBehaviorSnapshot)stageSSnapshot.BehaviorExtension;
                    return CountChildInstances(children, repeatingPlanItemId) == 2;
                },
                TimeSpan.FromSeconds(30));

            secondChildSpawned.Should().BeFalse(
                "#178: StageBehavior.HandleChildRepeated must consult Host.State.PlanItemState " +
                "before calling CreateChild - a Terminated stage must not create new repetition children. " +
                $"(Diagnostic: T's own Repeated flag reached true = {tRepeatedAfterTermination} before this check, " +
                "confirming the repetition-triggering event was genuinely published, not merely never sent.)");

            (await stageSGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Terminated,
                "StageS must still be Terminated - unaffected by the (refused) spawn attempt");
        }

        private async Task<string> ResolveTopLevelInstanceId(Guid caseInstanceId, string planItemId, int repetition)
        {
            var caseSnapshot = await _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope).GetSnapshot();

            if (caseSnapshot.BehaviorExtension?.Children.TryGetValue(planItemId, out var instances) != true)
            {
                throw new InvalidOperationException($"no top-level child instance of PlanItem '{planItemId}' has been created yet");
            }

            var instanceId = instances.Where(kvp => kvp.Value == repetition).Select(kvp => kvp.Key).SingleOrDefault();

            return instanceId ?? throw new InvalidOperationException(
                $"PlanItem '{planItemId}' has no repetition {repetition} instance (known repetitions: {string.Join(",", instances.Values)})");
        }

        private static int CountChildInstances(StageBehaviorSnapshot behaviorExtension, string planItemId) =>
            behaviorExtension?.Children != null && behaviorExtension.Children.TryGetValue(planItemId, out var instances)
                ? instances.Count
                : 0;

        private static string ResolveInstanceId(StageBehaviorSnapshot behaviorExtension, string planItemId, int repetition)
        {
            if (behaviorExtension?.Children == null || !behaviorExtension.Children.TryGetValue(planItemId, out var instances))
            {
                throw new InvalidOperationException($"no child instance of PlanItem '{planItemId}' has been created yet");
            }

            var instanceId = instances.Where(kvp => kvp.Value == repetition).Select(kvp => kvp.Key).SingleOrDefault();

            return instanceId ?? throw new InvalidOperationException(
                $"PlanItem '{planItemId}' has no repetition {repetition} instance (known repetitions: {string.Join(",", instances.Values)})");
        }

        private async Task<IPlanItemInternalGrain> FindRepetitionGrain(Guid caseInstanceId, string scope, string planItemId, int repetition)
        {
            var caseSnapshot = await _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope).GetSnapshot();

            if (caseSnapshot.BehaviorExtension?.Children.TryGetValue(planItemId, out var instances) != true) return null;

            var instanceId = instances.Where(kvp => kvp.Value == repetition).Select(kvp => kvp.Key).SingleOrDefault();

            return instanceId == null
                ? null
                : _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{scope}.{instanceId}");
        }

        private async Task<IPlanItemInternalGrain> PollUntilGrainFound(Guid caseInstanceId, string scope, string planItemId, int repetition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var grain = await FindRepetitionGrain(caseInstanceId, scope, planItemId, repetition);
                if (grain != null) return grain;
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            return await FindRepetitionGrain(caseInstanceId, scope, planItemId, repetition);
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
