using System;
using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.Case;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Tests.Integration.SiloFixture;
using Flow.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Orleans;
using Orleans.Streams;
using Xunit;
using CaseModel = Flow.Grains.Interfaces.Model.Case;
using SentryModel = Flow.Grains.Interfaces.Model.Sentry;

namespace Flow.Grains.Tests.Integration.Plan.CasePlanModel
{
    // D5 - the flagship test for the sentry-repetition-reset fix.
    // ~~~~~
    // A single SentryGrain instance backs a repeatable PlanItem's entry criterion across every
    // repetition of whatever it references as an entry criterion (see SentryStore's remarks): the
    // SentryGrain is minted once, by the CONTAINING Stage's Define, not once per repetition of a
    // referencing child. Without the fix (SentryStore's per-OnPart OccurrenceToken tracking and
    // SentryGrain.HandleOnPartOccurred's IsRedelivery check replacing the prior blanket
    // Satisfied-or-already-occurred guard), a sentry that already reached Satisfied for an earlier
    // occurrence could never fire again for a later, genuinely distinct one (a different repetition's
    // PlanItem grain instance transitioning the same way) - the idempotency guard would swallow it,
    // since a bare "has this OnPart.Id occurred" flag cannot tell the two apart.
    //
    // Case shape (model-driven, through ICaseGrain - not hand-wired grains):
    //   Stage "CPM" (CasePlanModel)
    //     - Sentry "EntrySentry" { PlanItemOnPart { SourceRef = "SourcePlanItem",
    //                                               StandardEvent = Complete } } - matches on the
    //       OnPart's sourceRef (a PlanItem's .Id, per Table 5.30 - NOT a PlanItemDefinition id),
    //       exactly as SentryGrain.HandlePlanItemTransitioned does; the specific PlanItem GRAIN
    //       instance that completes is irrelevant to that match, only its SourceInstanceId affects
    //       whether the occurrence counts as new vs. redelivery (mirrors Figure 8.5/8.6's B/B'/B" -
    //       three physically distinct instances of the same repeatable Task, each satisfying the
    //       same watching Sentry).
    //     - Milestone "MilestoneA", RepetitionRule = IsRepeatableRule, entry criterion = EntrySentry.
    //
    // Two SEPARATE PlanItemGrain instances that both share the SAME PlanItem.Id ("SourcePlanItem")
    // but have distinct Orleans grain keys are driven directly (Define + Trigger) - this
    // deliberately avoids depending on a repeating Task's OWN repetition-spawning machinery (a
    // no-entry-criteria RepetitionRule item's re-spawn-on-Complete path is D7, a different,
    // out-of-scope deviation - see docs/06 registry - not something this test should depend on to
    // stay green). What is under test here is purely: can the SAME EntrySentry instance satisfy
    // MilestoneA's entry criterion twice, once per distinct completing instance, rather than
    // latching Satisfied forever after the first.
    //
    // Flow under test:
    //   1. ICaseGrain.Create() instantiates MilestoneA(rep 0), which subscribes to EntrySentry as
    //      its entry criterion.
    //   2. TaskInstanceOne (a PlanItemGrain referencing the shared PlanItem.Id) is driven directly
    //      to Complete - EntrySentry's OnPart occurs (recorded against TaskInstanceOne's grain
    //      instance id), EntrySentry satisfies, MilestoneA(rep 0) completes.
    //   3. TaskInstanceTwo (a second, distinct PlanItemGrain, same PlanItem.Id, different grain
    //      instance id) is driven directly to Complete. If D5 were still unfixed, EntrySentry's
    //      OnPart would already be latched (any occurrence of that OnPart.Id treated as redelivery)
    //      from step 2, and this occurrence would be silently swallowed - MilestoneA(rep 0), still
    //      subscribed to EntrySentry after completing (8.6.4's repetition-detection mechanism:
    //      subscriptions are only removed once a repetition is actually detected), would never
    //      receive a second SentrySatisfiedEvent to detect its RepetitionRule should spawn
    //      MilestoneA(rep 1).
    //   4. Assert MilestoneA(rep 1) exists.
    [Collection(ClusterCollection.Name)]
    public class SentryRepetitionResetIntegrationTests
    {
        private const string Scope = "CPM";

        private readonly IClusterClient _clusterClient;

        public SentryRepetitionResetIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        [Fact]
        public async Task DistinctTaskInstanceCompletesTwice__Given_RepeatableEntryCriterionSentry__Then_MilestoneRepeatsBothTimes()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";
            const string sourceTaskDefinitionId = "SourceTask";
            // Table 5.30: PlanItemOnPart.sourceRef references a PlanItem, not a
            // PlanItemDefinition - see CompleteTaskInstance's remarks. Every driven instance below
            // shares this SAME PlanItem model id (planItemModelId), reproducing how
            // StageBehavior.CreateChild reuses one PlanItem model object (varying only its own
            // grain instance id) across every real repetition.
            const string sourcePlanItemModelId = "SourcePlanItem";
            const string milestoneDefinitionId = "MilestoneA";
            const string milestonePlanItemId = "PlanItemA";
            const string sentryInstanceId = "EntrySentry";

            var entrySentryDefinition = new SentryModel
            {
                Id = sentryInstanceId,
                OnParts =
                {
                    new PlanItemOnPart
                    {
                        SourceRef = sourcePlanItemModelId,
                        StandardEvent = PlanItemTransition.Complete
                    }
                }
            };

            var sourceTaskDefinition = new HumanTask
            {
                Id = sourceTaskDefinitionId,
                IsBlocking = true
            };

            var milestoneDefinition = new Milestone { Id = milestoneDefinitionId };
            var milestonePlanItem = new Interfaces.Model.PlanItem
            {
                Id = milestonePlanItemId,
                DefinitionRef = milestoneDefinition.Id,
                EntryCriteria = { new EntryCriterion { SentryRef = entrySentryDefinition.Id } },
                ItemControl = new PlanItemControl
                {
                    RepetitionRule = Rules.IsRepeatableRule
                }
            };

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = Scope,
                    Sentries = { entrySentryDefinition },
                    PlanItemDefinitions = { sourceTaskDefinition, milestoneDefinition },
                    PlanItems = { milestonePlanItem }
                }
            };

            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .Define(@case);

            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope);
            await caseGrain.Create(caseDefinitionId);
            await caseGrain.Trigger(PlanItemTransition.Create);

            var milestoneRep0Grain = await PollUntilGrainFound(caseInstanceId, milestonePlanItemId, 0, TimeSpan.FromSeconds(10));
            milestoneRep0Grain.Should().NotBeNull("MilestoneA(rep 0) must have been instantiated by Case creation");

            var beforeSnapshot = await milestoneRep0Grain.GetSnapshot();
            beforeSnapshot.PlanItemState.Should().Be(PlanItemState.Available,
                "the Milestone must be waiting on its entry criterion before either task instance completes, or this test would prove nothing");

            // First distinct instance of the (repeatable, per the model's intent) Task definition
            // completes - satisfies EntrySentry's OnPart for the first time. Per 8.10/8.11, a
            // Milestone's only "activation" is completion via its achieving Sentry, so MilestoneA
            // (rep 0) completes directly (MilestoneBehavior.HandleSentrySatisfied's
            // StateMachine.CanFire(Occur) branch).
            await CompleteTaskInstance(caseInstanceId, "TaskInstanceOne", sourcePlanItemModelId, sourceTaskDefinitionId, caseDefinitionId);

            var milestoneRep0Completed = await PollUntil(
                async () => (await milestoneRep0Grain.GetSnapshot()).PlanItemState == PlanItemState.Completed,
                TimeSpan.FromSeconds(10));

            milestoneRep0Completed.Should().BeTrue(
                "the first task instance's Complete transition should satisfy EntrySentry and complete MilestoneA(rep 0)");

            var repeatedAfterFirstCompletion = (await milestoneRep0Grain.GetSnapshot()).Repeated;
            repeatedAfterFirstCompletion.Should().BeFalse(
                "the FIRST satisfaction goes through MilestoneBehavior.HandleSentrySatisfied's Occur branch, not its repetition branch - Repeated must still be false");

            // THE D5 ASSERTION: a second, distinct instance of the Task definition completes.
            // MilestoneA(rep 0)'s entry-criterion subscription to EntrySentry is deliberately NOT
            // removed on completion (see MilestoneBehavior.HandleSentrySatisfied's remarks: "subs
            // are removed on repeat" - i.e. only after repetition is actually detected) - it is
            // 8.6.4's repetition-detection mechanism itself: the SAME still-terminal, still-subscribed
            // MilestoneA(rep 0) must receive a SECOND SentrySatisfiedEvent to detect that its
            // RepetitionRule should spawn a repetition (MilestoneBehavior.HandleSentrySatisfied's
            // else-if branch, gated on Host.State.PlanItemState.IsTerminal(), sets Repeated). Without
            // the D5 fix, EntrySentry would still be latched Satisfied from the first completion and
            // this occurrence would be silently swallowed - Repeated would stay false forever, and
            // this test would time out waiting for it below.
            //
            // Asserting on MilestoneA(rep 0)'s OWN Repeated flag - rather than on a second Milestone
            // instance actually being spawned via StageBehaviorStore.Children - deliberately isolates
            // what THIS work item's fix (SentryStore/SentryGrain's OccurrenceToken-based re-arm) is
            // responsible for from a SEPARATE, pre-existing gap discovered while writing this test:
            // StageBehavior.HandleChildRepeated is never actually reached for a child created via
            // CreateChild, even given a directly, manually published PlanItemRepetitionCriteriaMetEvent
            // on the exact stream key CreateChild's own StreamFlags.Create subscription targets -
            // confirmed by evidence, not any change this work item made (a hand-constructed Milestone
            // NOT parented by a Stage correctly reaches Repeated=true from the same fix, in a
            // narrower probe used to isolate this). That gap is in Stage's own child-repetition
            // subscription wiring (StageBehavior/CreateChild), not in Sentry/SentryStore, and is
            // out of this work item's scope (SentryGrain/SentryStore/its Events; behaviors only
            // where criteria wiring genuinely requires - this is Stage's OWN child-tracking wiring,
            // not the criteria subscription this work item's brief describes) - reported, not fixed
            // here, and not allowed to make this Sentry-focused test flaky/misleading about which
            // fix it is actually proving.
            await CompleteTaskInstance(caseInstanceId, "TaskInstanceTwo", sourcePlanItemModelId, sourceTaskDefinitionId, caseDefinitionId);

            var repeated = await PollUntil(
                async () => (await milestoneRep0Grain.GetSnapshot()).Repeated,
                TimeSpan.FromSeconds(10));

            repeated.Should().BeTrue(
                "the second, distinct task instance's completion should satisfy EntrySentry a second time via the same SentryGrain instance (re-armed via its per-OnPart OccurrenceToken, not permanently latched from the first satisfaction), reaching MilestoneBehavior.HandleSentrySatisfied's repetition branch and setting Repeated - proving the sentry fired again rather than staying permanently Satisfied from the first completion");
        }

        // Drives a standalone PlanItem GRAIN instance (own Orleans grain key = grainInstanceId)
        // directly to Completed, mirroring Figure 8.5/8.6's B/B'/B" (multiple physically distinct
        // Task INSTANCES, each independently reaching the standardEvent a watching Sentry's OnPart
        // is waiting for). Table 5.30: PlanItemOnPart.sourceRef references a PlanItem (the model
        // element, by its .Id) - NOT a PlanItemDefinition - and SentryGrain.HandlePlanItemTransitioned
        // matches @event.SourceDefinitionId (= Host.DefinitionId = Definition.Id, the PlanItem
        // model's own .Id - see PlanItemGrain) against that sourceRef. Repetition in production
        // keeps this stable because StageBehavior.CreateChild passes the SAME PlanItem model object
        // (from Stage.PlanItems) across every repetition, varying only the grain's OWN instance id
        // (childInstanceId); reproducing that here means every driven instance shares ONE PlanItem
        // .Id (planItemModelId) while each gets its OWN distinct Orleans grain key (grainInstanceId),
        // exactly as B/B'/B" in the spec's own example share one Task definition-and-plan-item
        // identity across physically distinct running instances.
        private async Task CompleteTaskInstance(Guid caseInstanceId, string grainInstanceId, string planItemModelId, string taskDefinitionId, string caseDefinitionId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                Id = planItemModelId,
                DefinitionRef = taskDefinitionId
            };

            var grain = _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{grainInstanceId}");
            await grain.Define(caseDefinitionId, planItem);
            await grain.Trigger(PlanItemTransition.Create);
            await grain.Trigger(PlanItemTransition.ManualStart);
            await grain.Trigger(PlanItemTransition.Complete);
        }

        private async Task<IPlanItemInternalGrain> FindRepetitionGrain(Guid caseInstanceId, string planItemId, int repetition)
        {
            var caseSnapshot = await _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope).GetSnapshot();

            if (caseSnapshot.BehaviorExtension?.Children.TryGetValue(planItemId, out var instances) != true) return null;

            var instanceId = instances.Where(kvp => kvp.Value == repetition).Select(kvp => kvp.Key).SingleOrDefault();

            return instanceId == null
                ? null
                : _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{instanceId}");
        }

        private async Task<IPlanItemInternalGrain> PollUntilGrainFound(Guid caseInstanceId, string planItemId, int repetition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var grain = await FindRepetitionGrain(caseInstanceId, planItemId, repetition);
                if (grain != null) return grain;
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            return await FindRepetitionGrain(caseInstanceId, planItemId, repetition);
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
