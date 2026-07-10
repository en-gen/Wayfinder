using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.Case;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Orleans;
using Xunit;
using CaseModel = Flow.Grains.Interfaces.Model.Case;
using SentryModel = Flow.Grains.Interfaces.Model.Sentry;

namespace Flow.Grains.Tests.Integration.Plan.CasePlanModel
{
    // Bug #55 - CasePlanModelBehavior stub: ICaseGrain.Create() never instantiated child plan
    // items.
    // ~~~~~
    // 8.4.1/Table 8.6: the CasePlanModel's outermost Stage instance MUST NOT contain entry
    // criteria and transitions Uninitialized -> Active directly on the create trigger, skipping
    // Available entirely (unlike an ordinary Stage/Task - Table 8.8 - which goes
    // Uninitialized -[create]-> Available -[start/manual start]-> Active). 8.7 Planning: "If a
    // Stage instance is in Active state, then the planned PlanItems are instantiated immediately
    // after planning completes" - the same instantiation StageBehavior.HandleEnterActiveFromStart
    // already performs for an ordinary Stage entering Active via start/manual start, just reached
    // by the CasePlanModel's own, spec-mandated route into that same state. Before this fix,
    // CasePlanModelBehavior never attached a handler to that arrival, so nothing happened.
    //
    // These tests exercise the fix through the PUBLIC surface only (ICaseGrain.Create() +
    // Trigger(Create)) - the front door CaseFileItemSentryIntegrationTests' class remarks
    // document as previously a dead end, and which its tests deliberately routed around by
    // instantiating PlanItem/Sentry grains directly. This is the first test in this repo where a
    // modeled case RUNS from case creation.
    //
    // Addressing note (resolved during authoring, in the code's favor): StageBehavior.CreateChild
    // addresses each child at "{Scope}.{freshly-generated ShortGuid}", NOT "{Scope}.{PlanItem.Id}"
    // - the child's OWN PlanItem.Id from the case's PlanItems list is never reused as its
    // instance id. CaseFileItemSentryIntegrationTests' class remarks describe an "addressing
    // convention" that matches this only because those tests hand-pick the instance id
    // themselves when constructing PlanItem/Sentry grains directly; that claim was never
    // exercised against a real CreateChild call before this fix existed. These tests instead
    // discover the real, engine-assigned instance id via CaseSnapshot.BehaviorExtension.Children
    // (StageBehaviorStore's Children map, keyed by ChildCreated.PlanItemDefinitionId - which,
    // despite the name, CreateChild populates with the PlanItem's OWN Id, not its
    // DefinitionRef/PlanItemDefinition.Id - -> PlanItemInstanceId -> Repetition), then address
    // the child grain with that id - proving instantiation happened, without assuming an address
    // the engine does not actually use.
    [Collection(ClusterCollection.Name)]
    public class CasePlanModelInstantiationIntegrationTests
    {
        private const string Scope = "CPM";

        private readonly IClusterClient _clusterClient;

        public CasePlanModelInstantiationIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        // THE FLAGSHIP TEST - upgrades the D1/D2 flagship (CaseFileItemSentryIntegrationTests) from
        // direct-instantiation to true model-driven end-to-end.
        //
        // Case shape (the same shape the D1/D2 sentry tests build by hand, now reached through
        // ICaseGrain.Create() instead of manual PlanItem/Sentry grain construction):
        //   Case "CPM" (outermost Stage = CasePlanModel)
        //     - Sentry "EntrySentry" { CaseFileItemOnPart { SourceRef = "TheCaseFileItem",
        //                                                    StandardEvent = Update },
        //                              IfPart { ContextRef = "TheCaseFileItem",
        //                                       Condition = "value.amount > 100" } }
        //     - Milestone "MilestoneA", instantiated as PlanItem "PlanItemA"
        //           { EntryCriteria = [ { SentryRef = "EntrySentry" } ] }
        //
        // Flow under test:
        //   1. ICaseDefinitionGrain.Define(@case) publishes the case definition.
        //   2. ICaseGrain.Create(caseDefinitionId) raises CaseCreated and configures the
        //      CasePlanModelBehavior (CaseGrain.PostDefine) - state machine starts Uninitialized.
        //   3. ICaseGrain.Trigger(PlanItemTransition.Create) fires the CasePlanModel's own
        //      Uninitialized -> Active transition (Table 8.6), which - per the fix -
        //      instantiates PlanItemA via the same CreateChild/Define/Trigger(Create) path
        //      StageBehavior uses for its own children.
        //   4. Assert: PlanItemA is reachable (via the engine-assigned instance id recovered from
        //      CaseSnapshot.BehaviorExtension.Children) and is Available (its entry criterion has
        //      a Sentry, so 8.7 Table 8.7 keeps it in Available rather than auto-starting it).
        //   5. Drive the CaseFileItem: Create (amount=0, does not satisfy the Update-keyed
        //      OnPart), Update(amount=50) (OnPart occurs, IfPart false - must not fire),
        //      Update(amount=150) (OnPart occurs, IfPart true - sentry fires).
        //   6. Assert: MilestoneA completes (8.10/8.11 - a Milestone's only "activation" is
        //      completion via its entry criterion/achieving Sentry).
        //
        // If Bug #55 were still open, step 3 would produce a Case with zero plan items and step 4
        // would fail outright (BehaviorExtension.Children empty, no grain to resolve) - this test
        // would not reach the CaseFileItem-driving steps at all.
        [Fact]
        public async Task CaseCreate__Given_ModelWithMilestoneAndCaseFileItemSentry__Then_ChildInstantiatedAndModelRunsToCompletion()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";
            const string caseFileItemId = "TheCaseFileItem";
            const string sentryInstanceId = "EntrySentry";
            const string milestoneDefinitionId = "MilestoneA";
            const string planItemId = "PlanItemA";

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
                },
                IfPart = new IfPart
                {
                    ContextRef = caseFileItemId,
                    Condition = new Expression
                    {
                        Language = ExpressionLanguage.Jint,
                        Body = "value.amount > 100"
                    }
                }
            };

            var milestoneDefinition = new Milestone { Id = milestoneDefinitionId };
            var planItem = new Interfaces.Model.PlanItem
            {
                Id = planItemId,
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

            // THE FRONT DOOR: define and trigger the Case through ICaseGrain only - no direct
            // PlanItem/Sentry grain construction anywhere in this test.
            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope);
            await caseGrain.Create(caseDefinitionId);
            var afterCreateSnapshot = await caseGrain.Trigger(PlanItemTransition.Create);

            afterCreateSnapshot.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.6: the CasePlanModel's create transition goes straight to Active, skipping Available");

            // Assert the child was instantiated: recover the engine-assigned instance id from
            // StageBehaviorStore's Children tracking, keyed by the PlanItem's own Id (see class
            // remarks on ChildCreated.PlanItemDefinitionId's actual semantics) - CreateChild
            // assigns its own fresh instance id, it does not reuse PlanItem.Id as the address.
            var milestoneGrain = ResolveChildGrain(caseInstanceId, afterCreateSnapshot, planItemId);

            var beforeSnapshot = await milestoneGrain.GetSnapshot();
            beforeSnapshot.PlanItemState.Should().Be(PlanItemState.Available,
                "the Milestone must have been instantiated by Case creation and be waiting on its entry criterion, not left Uninitialized (the bug) or already completed");
            beforeSnapshot.Definition.Id.Should().Be(planItemId);
            beforeSnapshot.PlanItemDefinition.Id.Should().Be(milestoneDefinitionId);

            // Drive the CaseFileItem the sentry above is waiting for.
            var caseFileItemGrain = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);
            await caseFileItemGrain.Create(caseDefinitionId, new Interfaces.Model.CaseFileItem { Id = caseFileItemId }, JsonNode.Parse("""{"amount": 0}"""));

            // amount=50: OnPart(Update) occurs but IfPart (value.amount > 100) is false - must
            // not satisfy the sentry.
            await caseFileItemGrain.Update(JsonNode.Parse("""{"amount": 50}"""));

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            var afterFirstUpdate = await milestoneGrain.GetSnapshot();
            afterFirstUpdate.PlanItemState.Should().Be(PlanItemState.Available,
                "the IfPart condition is false at amount=50, so the sentry must not fire yet");

            // amount=150: OnPart(Update) occurs again, IfPart now true - sentry fires, Milestone
            // completes.
            await caseFileItemGrain.Update(JsonNode.Parse("""{"amount": 150}"""));

            var completed = await PollUntil(
                async () => (await milestoneGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed,
                TimeSpan.FromSeconds(10));

            completed.Should().BeTrue(
                "the second Update transition should satisfy the sentry's OnPart and IfPart, completing the Milestone - proving the modeled case ran end-to-end from ICaseGrain.Create()");
        }

        // Narrower coverage: a CasePlanModel with several plan items (a Milestone and a
        // non-blocking HumanTask with no entry criteria) must instantiate ALL of them on the
        // Create-triggered entry to Active, not just the first - HandleEnterActiveFromStart's
        // Task.WhenAll(PlanItemDefinition.PlanItems.Select(...)) applies uniformly regardless of
        // how many children the CasePlanModel's Stage defines.
        [Fact]
        public async Task CaseCreate__Given_ModelWithMultiplePlanItems__Then_AllChildrenInstantiated()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            var milestoneDefinition = new Milestone { Id = "MilestoneA" };
            var humanTaskDefinition = new HumanTask { Id = "TaskA", IsBlocking = false };

            var milestonePlanItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemMilestone",
                DefinitionRef = milestoneDefinition.Id
            };
            var taskPlanItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemTask",
                DefinitionRef = humanTaskDefinition.Id
            };

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = Scope,
                    PlanItemDefinitions = { milestoneDefinition, humanTaskDefinition },
                    PlanItems = { milestonePlanItem, taskPlanItem }
                }
            };

            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .Define(@case);

            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope);
            await caseGrain.Create(caseDefinitionId);
            var afterCreateSnapshot = await caseGrain.Trigger(PlanItemTransition.Create);

            var milestoneGrain = ResolveChildGrain(caseInstanceId, afterCreateSnapshot, milestonePlanItem.Id);
            var taskGrain = ResolveChildGrain(caseInstanceId, afterCreateSnapshot, taskPlanItem.Id);

            var milestoneSnapshot = await milestoneGrain.GetSnapshot();
            var taskSnapshot = await taskGrain.GetSnapshot();

            milestoneSnapshot.PlanItemState.Should().Be(PlanItemState.Available,
                "every PlanItem in the CasePlanModel's plan must be instantiated on Create, not just the first");
            // 8.6.2 ManualActivationRule / Table 5.51: absence of a ManualActivationRule is
            // considered TRUE (BaseBehavior.EvaluateManualActivationRule's default) - a HumanTask
            // with no entry criteria and no explicit ManualActivationRule therefore moves
            // Available -> Enabled (waiting for a Case worker), not straight to Active. Enabled
            // is only reachable at all if the HumanTask was instantiated in the first place, which
            // is what this assertion actually needs to prove.
            taskSnapshot.PlanItemState.Should().Be(PlanItemState.Enabled,
                "a HumanTask with no ManualActivationRule defaults to requiring manual activation (8.6.2/Table 5.51: absence is considered TRUE), reaching Enabled - only observable if it was instantiated at all");
        }

        // Narrower coverage: 5.4.9.2/8.7 - DiscretionaryItems are planned "to the discretion" of a
        // Case worker at run-time (8.7 Planning: "Users (Case workers) are said to 'plan' (in
        // run-time), when they select DiscretionaryItems from a PlanningTable"), and MUST NOT be
        // auto-instantiated alongside the fixed plan on entry to Active. This pins that the fix
        // does not over-instantiate: only Stage.PlanItems, never PlanningTable.DiscretionaryItems.
        [Fact]
        public async Task CaseCreate__Given_ModelWithPlanningTableDiscretionaryItem__Then_DiscretionaryItemNotInstantiated()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            var milestoneDefinition = new Milestone { Id = "MilestoneA" };
            var discretionaryTaskDefinition = new HumanTask { Id = "DiscretionaryTaskA", IsBlocking = false };

            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemA",
                DefinitionRef = milestoneDefinition.Id
            };

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = Scope,
                    PlanItemDefinitions = { milestoneDefinition, discretionaryTaskDefinition },
                    PlanItems = { planItem },
                    PlanningTable = new Interfaces.Model.PlanningTable
                    {
                        TableItems =
                        {
                            new DiscretionaryItem
                            {
                                Name = "DiscretionaryTaskA",
                                DefinitionRef = discretionaryTaskDefinition.Id
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
            var afterCreateSnapshot = await caseGrain.Trigger(PlanItemTransition.Create);

            // The fixed plan item must exist...
            var milestoneGrain = ResolveChildGrain(caseInstanceId, afterCreateSnapshot, planItem.Id);
            (await milestoneGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available);

            // ...but the discretionary item must not have been instantiated as a child: it never
            // appears in StageBehaviorStore's Children tracking (surfaced via
            // CaseSnapshot.BehaviorExtension.Children), because it was never passed to
            // CreateChild - only Stage.PlanItems was. There is no PlanItem anywhere in this case
            // whose own Id is "DiscretionaryTaskA" (only the DiscretionaryItem in the
            // PlanningTable references that HumanTaskDefinition by DefinitionRef), so this key's
            // absence directly reflects whether the discretionary item was (wrongly)
            // instantiated - it cannot appear by coincidence.
            afterCreateSnapshot.BehaviorExtension.Should().NotBeNull();
            afterCreateSnapshot.BehaviorExtension.Children.Should().NotContainKey(discretionaryTaskDefinition.Id,
                "PlanningTable.DiscretionaryItems are planned at the discretion of a Case worker (5.4.9.2/8.7) and MUST NOT be auto-instantiated alongside the fixed plan");
            afterCreateSnapshot.BehaviorExtension.Children.Should().ContainKey(planItem.Id,
                "the fixed PlanItem for the Milestone was declared directly in Stage.PlanItems and must have been instantiated");
            afterCreateSnapshot.BehaviorExtension.Children.Should().HaveCount(1,
                "exactly one child (the fixed PlanItem) should have been instantiated - the DiscretionaryItem must not add a second");
        }

        // Narrower coverage: the CasePlanModel's own transition sequence (Table 8.6) is distinct
        // from an ordinary Stage/Task (Table 8.8) - it never visits Available/Enabled at all.
        // Pins PlanItemStateMachine.ConfigureForCasePlanModel's Uninitialized -[Create]-> Active
        // wiring (as opposed to ConfigureForStageOrTask's Uninitialized -[Create]-> Available
        // -[Start/ManualStart]-> Active) via the observable CaseSnapshot.PlanItemState the public
        // surface exposes, without asserting on the state machine's internals directly.
        [Fact]
        public async Task CaseTrigger__Given_CreateTransition__Then_GoesDirectlyToActiveSkippingAvailable()
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

            var beforeTrigger = await caseGrain.GetSnapshot();
            beforeTrigger.PlanItemState.Should().Be(PlanItemState.Uninitialized,
                "Create() alone (without Trigger) must not have advanced the state machine");

            var afterTrigger = await caseGrain.Trigger(PlanItemTransition.Create);

            afterTrigger.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.6: the outermost Stage instance skips Available and MUST transition directly to Active, because it has no entry-criteria Sentry (8.4.1) - unlike Table 8.8's ordinary Stage/Task route through Available");
        }

        // Recovers the child grain the engine actually created, without assuming an address
        // convention: StageBehavior.CreateChild assigns each child a freshly generated instance
        // id (ShortGuid.NewGuid()), never the PlanItem's own Id, so the only reliable way to find
        // it is via StageBehaviorStore's Children tracking (surfaced on CaseSnapshot as
        // BehaviorExtension.Children, keyed by ChildCreated.PlanItemDefinitionId - which, despite
        // the name, CreateChild populates with the PlanItem's OWN Id from Stage.PlanItems, not
        // its DefinitionRef/PlanItemDefinition.Id -> PlanItemInstanceId -> Repetition), which
        // ChildCreated populates at the moment CreateChild runs.
        private IPlanItemInternalGrain ResolveChildGrain(Guid caseInstanceId, CaseSnapshot caseSnapshot, string planItemId)
        {
            caseSnapshot.BehaviorExtension.Should().NotBeNull(
                "the CasePlanModelBehavior's StageBehaviorStore must have been populated by Create if any child was instantiated");
            caseSnapshot.BehaviorExtension.Children.Should().ContainKey(planItemId,
                $"a child instance of PlanItem '{planItemId}' must have been created on entry to Active");

            var instanceId = caseSnapshot.BehaviorExtension.Children[planItemId].Keys.Single();

            return _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{instanceId}");
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
