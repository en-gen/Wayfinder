using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.Case;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.Sentry;
using Flow.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Orleans;
using Xunit;
using CaseModel = Flow.Grains.Interfaces.Model.Case;
using SentryModel = Flow.Grains.Interfaces.Model.Sentry;

namespace Flow.Grains.Tests.Integration.Plan.CaseFileItem
{
    // 8.3 CaseFileItem Lifecycle - the D2 unlock, proven end-to-end.
    // ~~~~~
    // Before this work item, nothing published CaseFileItemTransitionedEvent, so
    // SentryGrain.HandleCaseFileItemTransitioned (a CaseFileItemOnPart subscription) and
    // TimerEventListenerBehavior's CaseFileItemStartTrigger subscription - both of which already
    // existed and already subscribed correctly - could never actually fire. These tests define a
    // real case-plan-model instance (Stage scope "CPM" containing a Milestone with an entry
    // criterion, plus a Sentry with a CaseFileItemOnPart), perform a CaseFileItem operation
    // through CaseFileItemGrain, and assert the sentry fires and the plan item actually
    // activates - proof the wiring works beginning to end, not just that an event gets published
    // into the void.
    //
    // These tests deliberately do NOT go through ICaseGrain.Create()/Trigger(): while writing this
    // suite, that path was found to never actually instantiate the CasePlanModel's child PlanItems
    // (CasePlanModelBehavior - src/Flow.Grains/Plan/PlanItem/Behaviors/CasePlanModelBehavior.cs -
    // is an empty stub inheriting StageBehavior with only a `// TODO!` comment, and the
    // CasePlanModel state machine transitions Uninitialized->Active directly via Create, which is
    // not the trigger StageBehavior.HandleEnterActiveFromStart's child-creation logic is wired to
    // - that only fires OnEntryFromAsync(Start/ManualStart, ...)). That gap is pre-existing (no
    // test ever exercised ICaseGrain.Create() before this work item; PlanItemGrainTests.cs already
    // establishes the precedent of driving PlanItemGrain instances directly instead) and is a
    // separate, much larger architectural gap (the modeling/instantiation layer, not the
    // event-driven CaseFileItem runtime this work item adds) - see this work item's report for
    // what remains. These tests instead instantiate the Milestone and Sentry directly at the
    // addresses StageBehavior.Define()/CreateChild would have used, so the actual thing under
    // test - does a CaseFileItem transition reach an already-subscribed sentry and activate an
    // already-waiting plan item - is exercised exactly as it would be in a fully working case.
    [Collection(ClusterCollection.Name)]
    public class CaseFileItemSentryIntegrationTests
    {
        private const string Scope = "CPM";

        private readonly IClusterClient _clusterClient;

        public CaseFileItemSentryIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        // THE FLAGSHIP TEST.
        //
        // Case shape:
        //   Stage "CPM" (scope)
        //     - Sentry "EntrySentry" { CaseFileItemOnPart { SourceRef = "TheCaseFileItem",
        //                                                   StandardEvent = Create } }
        //     - Milestone "MilestoneA", instantiated as PlanItem "PlanItemA"
        //           { EntryCriteria = [ { SentryRef = "EntrySentry" } ] }
        //
        // Flow under test:
        //   1. PlanItemA (Milestone) is defined and Trigger(Create)'d - MilestoneBehavior enters
        //      Available and subscribes its EntryCriteria (SubscribeToCriteria).
        //   2. EntrySentry is defined as a SentryGrain at "CPM.EntrySentry" - its OnActivateAsync/
        //      Define subscribes its CaseFileItemOnPart via
        //      SentryGrain.SubscribeToOnPartTransitions
        //      (SubscribeTo<CaseFileItemTransitionedEvent>("TheCaseFileItem", ...)).
        //   3. Test calls CaseFileItemGrain.Create(...) for "TheCaseFileItem" - this is the exact
        //      call that was previously impossible to observe downstream of, because nothing
        //      published the event the sentry above is waiting for.
        //   4. SentryGrain.HandleCaseFileItemTransitioned matches the Create standardEvent against
        //      its lone CaseFileItemOnPart, the OnPart occurs, the Sentry (no IfPart) is
        //      immediately Satisfied, and publishes SentrySatisfiedEvent.
        //   5. MilestoneBehavior.HandleSentrySatisfied receives it, matches its EntryCriterion by
        //      SentryRef, and fires PlanItemTransition.Occur - Milestone goes straight to
        //      Completed (8.10/8.11: Milestone's only "activation" is completion).
        //
        // If D2 were still unresolved, step 3 would be a total no-op and the Milestone would sit
        // in Available forever - this test would time out waiting for it.
        [Fact]
        public async Task CaseFileItemCreate__Given_SentryWithMatchingCaseFileItemOnPart__Then_SentryFiresAndPlanItemActivates()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";
            const string caseFileItemId = "TheCaseFileItem";
            const string sentryInstanceId = "EntrySentry";

            var entrySentryDefinition = new SentryModel
            {
                Id = sentryInstanceId,
                OnParts =
                {
                    new CaseFileItemOnPart
                    {
                        SourceRef = caseFileItemId,
                        StandardEvent = CaseFileItemTransition.Create
                    }
                }
            };

            var milestoneDefinition = new Milestone { Id = "MilestoneA" };
            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemA",
                DefinitionRef = milestoneDefinition.Id,
                EntryCriteria =
                {
                    new EntryCriterion { SentryRef = entrySentryDefinition.Id }
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
                    PlanItemDefinitions = { milestoneDefinition },
                    PlanItems = { planItem }
                }
            };

            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .Define(@case);

            var milestoneGrain = _clusterClient
                .GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{planItem.Id}");
            await milestoneGrain.Define(caseDefinitionId, planItem);
            await milestoneGrain.Trigger(PlanItemTransition.Create);

            var sentryGrain = _clusterClient
                .GetGrain<ISentryGrain>(caseInstanceId, $"{Scope}.{sentryInstanceId}");
            await sentryGrain.Define(caseDefinitionId, entrySentryDefinition);

            // sanity check: the milestone must actually be waiting (Available), not already
            // completed, before the case-file operation happens - otherwise this test would
            // trivially pass for the wrong reason.
            var beforeSnapshot = await milestoneGrain.GetSnapshot();
            beforeSnapshot.PlanItemState.Should().Be(PlanItemState.Available,
                "the Milestone must be waiting on its entry criterion before the case-file operation, or this test would prove nothing");

            // THE D2 UNLOCK: perform the case-file operation the sentry above is waiting for.
            var caseFileItemGrain = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);
            await caseFileItemGrain.Create(caseDefinitionId, new Interfaces.Model.CaseFileItem { Id = caseFileItemId }, JsonValue.Create("filed"));

            var completed = await PollUntil(
                async () => (await milestoneGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed,
                TimeSpan.FromSeconds(10));

            completed.Should().BeTrue("the sentry's CaseFileItemOnPart should have fired from the CaseFileItem Create transition, satisfying the entry criterion and completing the Milestone");

            var afterSnapshot = await milestoneGrain.GetSnapshot();
            afterSnapshot.PlanItemState.Should().Be(PlanItemState.Completed);
        }

        // Same shape, but the sentry's CaseFileItemOnPart standardEvent is Update, not Create -
        // covers a case-file operation other than the initial creation actually satisfying a
        // sentry, and pins that Create alone does NOT spuriously satisfy an Update-keyed OnPart.
        [Fact]
        public async Task CaseFileItemUpdate__Given_SentryWithMatchingCaseFileItemOnPart__Then_SentryFiresAndPlanItemActivates()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";
            const string caseFileItemId = "TheCaseFileItem";
            const string sentryInstanceId = "EntrySentry";

            var entrySentryDefinition = new SentryModel
            {
                Id = sentryInstanceId,
                OnParts =
                {
                    new CaseFileItemOnPart
                    {
                        SourceRef = caseFileItemId,
                        StandardEvent = CaseFileItemTransition.Update
                    }
                }
            };

            var milestoneDefinition = new Milestone { Id = "MilestoneA" };
            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemA",
                DefinitionRef = milestoneDefinition.Id,
                EntryCriteria =
                {
                    new EntryCriterion { SentryRef = entrySentryDefinition.Id }
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
                    PlanItemDefinitions = { milestoneDefinition },
                    PlanItems = { planItem }
                }
            };

            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .Define(@case);

            var milestoneGrain = _clusterClient
                .GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{planItem.Id}");
            await milestoneGrain.Define(caseDefinitionId, planItem);
            await milestoneGrain.Trigger(PlanItemTransition.Create);

            var sentryGrain = _clusterClient
                .GetGrain<ISentryGrain>(caseInstanceId, $"{Scope}.{sentryInstanceId}");
            await sentryGrain.Define(caseDefinitionId, entrySentryDefinition);

            var caseFileItemGrain = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);
            await caseFileItemGrain.Create(caseDefinitionId, new Interfaces.Model.CaseFileItem { Id = caseFileItemId }, JsonValue.Create("initial"));

            // Create alone must not satisfy an Update-keyed OnPart.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            var afterCreate = await milestoneGrain.GetSnapshot();
            afterCreate.PlanItemState.Should().Be(PlanItemState.Available,
                "a Create transition must not satisfy a sentry OnPart keyed to the Update standardEvent");

            await caseFileItemGrain.Update(JsonValue.Create("updated"));

            var completed = await PollUntil(
                async () => (await milestoneGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed,
                TimeSpan.FromSeconds(10));

            completed.Should().BeTrue("the sentry's CaseFileItemOnPart(Update) should have fired from the CaseFileItem Update transition");
        }

        // 8.3 / Table 5.19 TimerEventListener.timerStart - a TimerEventListener with a
        // CaseFileItemStartTrigger now arms from a case-file event.
        // TimerEventListenerBehavior.HandleEnterAvailableFromCreate already subscribes to
        // CaseFileItemTransitionedEvent via the CaseFileItemStartTrigger arm of its switch (see
        // TimerEventListenerBehavior.cs) - this existed before this work item and, like the
        // Sentry case, could never fire. This proves it now does: the TimerEventListener's
        // TimerStartTriggerOccurred event is raised (captured via GetSnapshot's
        // BehaviorExtension), which is what the timer's scheduling is relative to (8.3/Table 5.19:
        // "timerExpression SHOULD be relative to the timestamp captured when the timerStart
        // trigger occurs").
        [Fact]
        public async Task CaseFileItemCreate__Given_TimerEventListenerWithMatchingCaseFileItemStartTrigger__Then_TimerStartTriggerOccurs()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";
            const string caseFileItemId = "TheCaseFileItem";

            var timerEventListener = new TimerEventListener
            {
                Id = "TimerA",
                // 1 hour after the trigger occurs - the point under test is that the trigger
                // itself fires and is captured (BehaviorExtension.TimerStart), not the eventual
                // timer tick.
                TimerExpression = new Expression
                {
                    Language = ExpressionLanguage.Jint,
                    Body = "'PT1H'"
                },
                TimerStart = new CaseFileItemStartTrigger
                {
                    SourceRef = caseFileItemId,
                    StandardEvent = CaseFileItemTransition.Create
                }
            };

            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "TimerPlanItemA",
                DefinitionRef = timerEventListener.Id
            };

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = Scope,
                    PlanItemDefinitions = { timerEventListener },
                    PlanItems = { planItem }
                }
            };

            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .Define(@case);

            var timerPlanItemGrain = _clusterClient
                .GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{planItem.Id}");
            await timerPlanItemGrain.Define(caseDefinitionId, planItem);
            await timerPlanItemGrain.Trigger(PlanItemTransition.Create);

            var beforeSnapshot = await timerPlanItemGrain.GetSnapshot();
            var beforeBehavior = (Flow.Grains.Interfaces.Plan.PlanItem.Behaviors.TimerEventListenerBehaviorSnapshot)beforeSnapshot.BehaviorExtension;
            beforeBehavior.TimerStart.Should().BeNull("the CaseFileItemStartTrigger has not occurred yet");

            var caseFileItemGrain = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);
            await caseFileItemGrain.Create(caseDefinitionId, new Interfaces.Model.CaseFileItem { Id = caseFileItemId }, null);

            var occurred = await PollUntil(
                async () =>
                {
                    var snapshot = await timerPlanItemGrain.GetSnapshot();
                    var behavior = (Flow.Grains.Interfaces.Plan.PlanItem.Behaviors.TimerEventListenerBehaviorSnapshot)snapshot.BehaviorExtension;
                    return behavior.TimerStart.HasValue;
                },
                TimeSpan.FromSeconds(10));

            occurred.Should().BeTrue("TimerEventListenerBehavior's CaseFileItemStartTrigger subscription should have captured the CaseFileItem Create transition as the timer's start trigger");
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
