using System;
using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.Case;
using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Interfaces.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Tests.Integration.SiloFixture;
using Flow.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Orleans;
using Xunit;
using CaseModel = Flow.Grains.Interfaces.Model.Case;

namespace Flow.Grains.Tests.Integration.Plan.CasePlanModel
{
    // D4 - Table 8.12 (8.6.1) Stage instance termination criteria, exercised end-to-end through
    // real grains, model-driven (ICaseGrain.Create + Trigger only; nested stage and task grains
    // are resolved via the engine-assigned instance ids in the Children maps, never constructed
    // directly).
    //
    // Table 8.12's two columns, read precisely:
    //   autoComplete = TRUE : "There are no Active children, AND all required (requiredRule
    //     evaluates to TRUE) children are in {Disabled, Completed, Terminated, Failed}."
    //   autoComplete = FALSE: "(There are no Active children AND all children are in {Disabled,
    //     Completed, Terminated, Failed} AND there are no DiscretionaryItems) OR (Manual
    //     Completion AND all required (requiredRule evaluates to TRUE) children are in
    //     {Disabled, Completed, Terminated, Failed})."
    //
    // The matrix below covers autoComplete {true, false} x required-children {terminal, not
    // terminal} x discretionary-items {none, pending}, with the automatic branch observed via
    // stream-driven auto-completion and the manual branch via an explicit Trigger(Complete) on
    // the stage grain.
    //
    // Case shape (per scenario, fresh case instance):
    //   Case "CPM" (outermost Stage)
    //     PlanItemDefinitions: StageS (autoComplete varies), TaskR (HumanTask), TaskN (HumanTask)
    //         - definitions intentionally FLAT at the casePlanModel level:
    //           CaseDefinitionGrain.GetPlanItemDefinition resolves a nested instance's
    //           DefinitionRef by walking its runtime scope upwards, and runtime scopes carry
    //           instance ids, so the definition index is only hit at the case root
    //     PlanItems: PlanItemStage -> StageS
    //   StageS.PlanItems:
    //     PlanItemR -> TaskR, ItemControl.RequiredRule = Rules.IsRequiredRule (required)
    //     PlanItemN -> TaskN (no RequiredRule; Table 5.51 absence default = FALSE, not required)
    [Collection(ClusterCollection.Name)]
    public class StageCompletionRulesIntegrationTests
    {
        private const string Scope = "CPM";
        private const string StageDefinitionId = "StageS";
        private const string StagePlanItemId = "PlanItemStage";
        private const string RequiredTaskPlanItemId = "PlanItemR";
        private const string OptionalTaskPlanItemId = "PlanItemN";

        private readonly IClusterClient _clusterClient;

        public StageCompletionRulesIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        // Table 8.12, autoComplete=TRUE: required child terminal + non-required child merely
        // Enabled (NOT Active) -> the stage must complete AUTOMATICALLY, without any manual
        // trigger.
        [Fact]
        public async Task StageCompletion__Given_AutoComplete__When_RequiredTerminalAndNonRequiredEnabled__Then_AutoCompletes()
        {
            var setup = await CreateCaseWithStage(autoComplete: true);

            await CompleteTask(setup.RequiredTaskGrain);

            var completed = await PollUntil(
                async () => (await setup.StageGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed,
                TimeSpan.FromSeconds(10));

            completed.Should().BeTrue(
                "autoComplete=true requires only 'no Active children AND required children terminal' - the Enabled non-required task must not hold the stage open");
        }

        // Table 8.12, autoComplete=FALSE, Branch 1 not satisfied (non-required child still
        // Enabled, so NOT all children terminal) -> the stage must NOT auto-complete; Branch 2
        // (manual) then succeeds because every REQUIRED child is terminal.
        [Fact]
        public async Task StageCompletion__Given_NotAutoComplete__When_NonRequiredEnabled__Then_NoAutoCompleteButManualSucceeds()
        {
            var setup = await CreateCaseWithStage(autoComplete: false);

            await CompleteTask(setup.RequiredTaskGrain);

            // absence assertion: give the child-transitioned stream time to deliver, then confirm
            // the automatic branch did NOT fire
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            (await setup.StageGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active,
                "Branch 1 requires ALL children terminal - the Enabled non-required task must block automatic completion");

            var afterManualComplete = await setup.StageGrain.Trigger(PlanItemTransition.Complete);

            afterManualComplete.PlanItemState.Should().Be(PlanItemState.Completed,
                "Branch 2 (Manual Completion) requires only required children terminal");
        }

        // THE D4 HEADLINE: autoComplete=FALSE with a non-required child ACTIVE (not just
        // Enabled). Branch 2 carries no 'no Active children' conjunct, so manual completion must
        // succeed - this is exactly the "non-required active children wrongly block manual
        // completion" deviation.
        [Fact]
        public async Task StageCompletion__Given_NotAutoComplete__When_NonRequiredActive__Then_ManualCompleteSucceeds()
        {
            var setup = await CreateCaseWithStage(autoComplete: false);

            // start (but do not complete) the non-required task - it is now Active
            await setup.OptionalTaskGrain.Trigger(PlanItemTransition.ManualStart);
            (await setup.OptionalTaskGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active);

            await CompleteTask(setup.RequiredTaskGrain);

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            (await setup.StageGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active,
                "the automatic branch must not fire while any child is Active");

            var afterManualComplete = await setup.StageGrain.Trigger(PlanItemTransition.Complete);

            afterManualComplete.PlanItemState.Should().Be(PlanItemState.Completed,
                "Table 8.12's manual-completion branch requires only required children in {Disabled, Completed, Terminated, Failed} - an Active NON-required child must not block it");
        }

        // Counterpart: a REQUIRED child not yet terminal blocks manual completion loudly.
        [Fact]
        public async Task StageCompletion__Given_NotAutoComplete__When_RequiredNotTerminal__Then_ManualCompleteThrows()
        {
            var setup = await CreateCaseWithStage(autoComplete: false);

            // TaskR is still Enabled (never started, not terminal); TaskN untouched
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => setup.StageGrain.Trigger(PlanItemTransition.Complete));

            (await setup.StageGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active,
                "a rejected manual completion must leave the stage exactly where it was");
        }

        // Table 8.12, autoComplete=FALSE with a pending (unplanned) DiscretionaryItem: Branch 1
        // is blocked even though every child instance is terminal - but Branch 2 (manual) says
        // nothing about DiscretionaryItems and must succeed.
        [Fact]
        public async Task StageCompletion__Given_NotAutoComplete__When_DiscretionaryItemPending__Then_NoAutoCompleteButManualSucceeds()
        {
            var setup = await CreateCaseWithStage(autoComplete: false, discretionaryItem: true);

            await CompleteTask(setup.RequiredTaskGrain);
            await CompleteTask(setup.OptionalTaskGrain);

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            (await setup.StageGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active,
                "Branch 1 requires 'there are no DiscretionaryItems' - an unplanned discretionary item must block automatic completion");

            var afterManualComplete = await setup.StageGrain.Trigger(PlanItemTransition.Complete);

            afterManualComplete.PlanItemState.Should().Be(PlanItemState.Completed,
                "Branch 2 (Manual Completion) does not consider DiscretionaryItems");
        }

        // Table 8.12, autoComplete=FALSE, Branch 1 fully satisfied: all children terminal, no
        // planning table -> automatic completion, no manual trigger involved.
        [Fact]
        public async Task StageCompletion__Given_NotAutoComplete__When_AllChildrenTerminalNoDiscretionary__Then_AutoCompletes()
        {
            var setup = await CreateCaseWithStage(autoComplete: false);

            await CompleteTask(setup.RequiredTaskGrain);
            await CompleteTask(setup.OptionalTaskGrain);

            var completed = await PollUntil(
                async () => (await setup.StageGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed,
                TimeSpan.FromSeconds(10));

            completed.Should().BeTrue(
                "Branch 1: no Active children, all children terminal, no DiscretionaryItems - the stage must complete on its own");
        }

        private sealed class StageSetup
        {
            public IPlanItemInternalGrain StageGrain { get; init; }
            public IPlanItemInternalGrain RequiredTaskGrain { get; init; }
            public IPlanItemInternalGrain OptionalTaskGrain { get; init; }
        }

        private async Task<StageSetup> CreateCaseWithStage(bool autoComplete, bool discretionaryItem = false)
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            var requiredTaskDefinition = new HumanTask { Id = "TaskR", IsBlocking = true };
            var optionalTaskDefinition = new HumanTask { Id = "TaskN", IsBlocking = true };

            var stageDefinition = new Stage
            {
                Id = StageDefinitionId,
                AutoComplete = autoComplete,
                PlanItems =
                {
                    new Interfaces.Model.PlanItem
                    {
                        Id = RequiredTaskPlanItemId,
                        DefinitionRef = requiredTaskDefinition.Id,
                        ItemControl = new PlanItemControl
                        {
                            RequiredRule = Rules.IsRequiredRule
                        }
                    },
                    new Interfaces.Model.PlanItem
                    {
                        Id = OptionalTaskPlanItemId,
                        DefinitionRef = optionalTaskDefinition.Id
                    }
                }
            };

            if (discretionaryItem)
            {
                stageDefinition.PlanningTable = new Interfaces.Model.PlanningTable
                {
                    TableItems =
                    {
                        new DiscretionaryItem
                        {
                            Id = "DiscretionaryA",
                            Name = "DiscretionaryA",
                            DefinitionRef = optionalTaskDefinition.Id
                        }
                    }
                };
            }

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = Scope,
                    PlanItemDefinitions = { stageDefinition, requiredTaskDefinition, optionalTaskDefinition },
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

            // resolve the nested stage instance via the engine-assigned instance id
            var stageInstanceId = caseSnapshot.BehaviorExtension.Children[StagePlanItemId].Keys.Single();
            var stageGrain = _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{stageInstanceId}");

            // 8.6.2/Table 5.51: no ManualActivationRule -> default TRUE -> the stage waits
            // Enabled; ManualStart drives it Active, which instantiates its children
            (await stageGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Enabled);
            var stageSnapshot = await stageGrain.Trigger(PlanItemTransition.ManualStart);
            stageSnapshot.PlanItemState.Should().Be(PlanItemState.Active);

            var stageChildren = ((StageBehaviorSnapshot)stageSnapshot.BehaviorExtension).Children;
            var requiredTaskGrain = ResolveChild(caseInstanceId, stageInstanceId, stageChildren, RequiredTaskPlanItemId);
            var optionalTaskGrain = ResolveChild(caseInstanceId, stageInstanceId, stageChildren, OptionalTaskPlanItemId);

            // both tasks wait Enabled (manual activation default TRUE), with the required rule
            // evaluated on their Create -> Available transition (8.6.3)
            (await requiredTaskGrain.GetSnapshot()).Required.Should().BeTrue("PlanItemR carries a truthy RequiredRule");
            (await optionalTaskGrain.GetSnapshot()).Required.Should().BeFalse("Table 5.51: RequiredRule absence defaults to FALSE");

            return new StageSetup
            {
                StageGrain = stageGrain,
                RequiredTaskGrain = requiredTaskGrain,
                OptionalTaskGrain = optionalTaskGrain
            };
        }

        private IPlanItemInternalGrain ResolveChild(
            Guid caseInstanceId,
            string stageInstanceId,
            System.Collections.Generic.IDictionary<string, System.Collections.Generic.IDictionary<string, int>> stageChildren,
            string planItemId)
        {
            stageChildren.Should().ContainKey(planItemId);
            var instanceId = stageChildren[planItemId].Keys.Single();
            return _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{stageInstanceId}.{instanceId}");
        }

        private static async Task CompleteTask(IPlanItemInternalGrain taskGrain)
        {
            await taskGrain.Trigger(PlanItemTransition.ManualStart);
            await taskGrain.Trigger(PlanItemTransition.Complete);
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
