using System;
using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.Case;
using Wayfinder.Grains.Interfaces.Plan.PlanItem;
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
    // #179 - Table 8.9's `complete` rows mark a Completed Stage coexisting with a Stage or Task
    // child in {Available, Enabled, Active, Suspended} as IMPOSSIBLE - only {Disabled, Completed,
    // Terminated, Failed} may remain. (Milestone/EventListener have their OWN column in that same
    // table and legitimately survive - Available/Suspended are explicitly permitted there, per
    // Table 8.7's completed-Stage description naming only "Stage or Task instances"; those two
    // behaviors are deliberately untouched by this fix.) Table 8.12's autoComplete=TRUE completion
    // criteria ("no Active children AND all REQUIRED children terminal") says nothing about
    // non-required children, so a Stage can legitimately reach Completed while a non-required
    // Stage or Task child still sits Available/Enabled. The two tables only reconcile if
    // completion itself drives that remainder to a terminal state - StageBehavior AND
    // TaskBehavior's HandleParentTransitioned now both cascade Exit to any non-terminal child of a
    // completing parent, mirroring the pre-existing Exit/Terminate cascade (Table 8.9's
    // exit/terminate propagation) rather than inventing a second mechanism.
    //
    // Model-driven (ICaseGrain.Create + Trigger only), matching StageCompletionRulesIntegrationTests'
    // and SentryRepetitionResetIntegrationTests' conventions - not `.cmmn`-driven, so this lives
    // alongside its siblings here rather than in Conformance/*Scenarios.cs.
    [Collection(ClusterCollection.Name)]
    public class StageCompletionCascadeIntegrationTests
    {
        private const string Scope = "CPM";

        private readonly IClusterClient _clusterClient;

        public StageCompletionCascadeIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        }

        // Case shape (mirrors the original #179 reproduction):
        //   StageS (autoComplete=TRUE)
        //     PlanItemR       -> TaskR       (required, no criteria)
        //     PlanItemTrigger -> TaskTrigger (non-required, no criteria - left Enabled when StageS
        //                                     completes, so it doubles as this test's Task-typed
        //                                     cascade target. Its own completion is also StageB's
        //                                     INDEPENDENT entry criterion, unrelated to StageS's
        //                                     own completion - see the remarks below on why that
        //                                     criterion is deliberately never satisfied)
        //     PlanItemB       -> StageB (non-required, EntryCriterion -> IndependentSentry,
        //                                ManualActivationRule=FALSE so a satisfied entry criterion
        //                                drives Start directly)
        //   StageB (autoComplete=TRUE):
        //     PlanItemC -> TaskC (required) - would be instantiated the moment StageB reaches
        //                 Active (StageBehavior.HandleEnterActiveFromStart) - i.e. exactly the
        //                 "begins spawning its own children" half of the original defect.
        [Fact]
        public async Task StageCompletionCascade__Given_AutoCompleteStageWithNonRequiredChildrenAvailable__When_ParentCompletes__Then_StageAndTaskChildrenAreCascadedToTerminatedAndNeverActivate()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            var taskRDefinition = new HumanTask { Id = "TaskR", IsBlocking = true };
            var taskTriggerDefinition = new HumanTask { Id = "TaskTrigger", IsBlocking = true };
            var taskCDefinition = new HumanTask { Id = "TaskC", IsBlocking = true };

            var stageBDefinition = new Stage
            {
                Id = "StageB",
                AutoComplete = true,
                PlanItemDefinitions = { taskCDefinition },
                PlanItems =
                {
                    new Interfaces.Model.PlanItem
                    {
                        Id = "PlanItemC",
                        DefinitionRef = taskCDefinition.Id,
                        ItemControl = new PlanItemControl { RequiredRule = Rules.IsRequiredRule }
                    }
                }
            };

            var independentSentry = new SentryModel
            {
                Id = "IndependentSentry",
                OnParts =
                {
                    new PlanItemOnPart
                    {
                        SourceRef = "PlanItemTrigger",
                        StandardEvent = PlanItemTransition.Complete
                    }
                }
            };

            var stageSDefinition = new Stage
            {
                Id = "StageS",
                AutoComplete = true,
                Sentries = { independentSentry },
                PlanItemDefinitions = { taskRDefinition, taskTriggerDefinition, stageBDefinition },
                PlanItems =
                {
                    new Interfaces.Model.PlanItem
                    {
                        Id = "PlanItemR",
                        DefinitionRef = taskRDefinition.Id,
                        ItemControl = new PlanItemControl { RequiredRule = Rules.IsRequiredRule }
                    },
                    new Interfaces.Model.PlanItem
                    {
                        Id = "PlanItemTrigger",
                        DefinitionRef = taskTriggerDefinition.Id
                    },
                    new Interfaces.Model.PlanItem
                    {
                        Id = "PlanItemB",
                        DefinitionRef = stageBDefinition.Id,
                        EntryCriteria = { new EntryCriterion { SentryRef = independentSentry.Id } },
                        ItemControl = new PlanItemControl { ManualActivationRule = Rules.NotManuallyActivated }
                    }
                }
            };

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = Scope,
                    PlanItemDefinitions = { stageSDefinition },
                    PlanItems =
                    {
                        new Interfaces.Model.PlanItem { Id = "PlanItemStage", DefinitionRef = stageSDefinition.Id }
                    }
                }
            };

            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .Define(@case);

            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope);
            await caseGrain.Create(caseDefinitionId);
            var caseSnapshot = await caseGrain.Trigger(PlanItemTransition.Create);

            var stageSInstanceId = caseSnapshot.BehaviorExtension.Children["PlanItemStage"].Keys.Single();
            var stageSGrain = _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{stageSInstanceId}");

            (await stageSGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Enabled);
            var stageSSnapshot = await stageSGrain.Trigger(PlanItemTransition.ManualStart);
            stageSSnapshot.PlanItemState.Should().Be(PlanItemState.Active);

            var stageSChildren = ((StageBehaviorSnapshot)stageSSnapshot.BehaviorExtension).Children;

            var taskRGrain = ResolveChild(caseInstanceId, stageSInstanceId, stageSChildren, "PlanItemR");
            var taskTriggerGrain = ResolveChild(caseInstanceId, stageSInstanceId, stageSChildren, "PlanItemTrigger");
            var stageBGrain = ResolveChild(caseInstanceId, stageSInstanceId, stageSChildren, "PlanItemB");

            (await stageBGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available,
                "StageB is waiting on its own independent entry criterion - it must not be Enabled/Active yet, or this test proves nothing");

            // Complete the ONLY required child. TaskTrigger is merely Enabled and StageB is merely
            // Available, so Table 8.12's autoComplete=TRUE criteria ("no Active children AND all
            // required children terminal") are satisfied - StageS completes automatically.
            await taskRGrain.Trigger(PlanItemTransition.ManualStart);
            await taskRGrain.Trigger(PlanItemTransition.Complete);

            var stageSCompleted = await PollUntil(
                async () => (await stageSGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed,
                TimeSpan.FromSeconds(10));
            stageSCompleted.Should().BeTrue("autoComplete=true requires only required children terminal - TaskTrigger (Enabled) and StageB (Available) must not hold the stage open");

            // THE FIX: StageS completing must cascade Exit to StageB (Table 8.9's `complete` row -
            // Available cannot coexist with a Completed parent), not leave it dangling.
            var stageBTerminated = await PollUntil(
                async () => (await stageBGrain.GetSnapshot()).PlanItemState == PlanItemState.Terminated,
                TimeSpan.FromSeconds(10));
            stageBTerminated.Should().BeTrue(
                "#179 fix: StageS completing must cascade Exit to StageB per Table 8.9's `complete` row - Available cannot coexist with a Completed parent");

            // StageB never reached Active, so HandleEnterActiveFromStart never ran - TaskC (its
            // required child) can never have been instantiated. This is a direct structural
            // consequence of the state above, not a race - asserted unconditionally, no poll
            // needed.
            var stageBSnapshot = await stageBGrain.GetSnapshot();
            var stageBChildren = ((StageBehaviorSnapshot)stageBSnapshot.BehaviorExtension).Children;
            stageBChildren.Should().NotContainKey("PlanItemC",
                "StageB was cascaded out of Available before ever reaching Active, so it must never have instantiated its required child TaskC - the 'begins spawning its own children' half of #179's defect");

            // TaskBehavior carries the IDENTICAL Complete-cascade case (Table 8.9 puts Task in the
            // SAME `<impossible>` column as Stage - see StageBehavior.HandleParentTransitioned's
            // type-asymmetry remarks): TaskTrigger (non-required, Enabled, non-terminal) must be
            // cascaded too, not just StageB.
            //
            // Deliberately NOT manually completed here. An earlier draft of this test drove
            // TaskTrigger through ManualStart/Complete AFTER StageS had already completed, to
            // satisfy StageB's independent entry criterion and prove StageB stayed inert - but at
            // the time TaskBehavior itself was unfixed, so that call was silently exercising the
            // exact defect class #179 exists to close: a Task legitimately transitioning
            // Enabled -> Active -> Completed inside an already-Completed parent, live inside its
            // own regression test. Now that TaskBehavior is fixed, TaskTrigger is cascaded to
            // Terminated at the same moment as StageB, so StageB's independent entry criterion
            // (sourced from TaskTrigger's own completion) can never be satisfied by anything live
            // at all - its only possible driver is itself terminal. No separate "late
            // satisfaction is a no-op" poll is needed to prove StageB stays inert:
            // PlanItemStateMachine.ConfigureForStageOrTask configures ZERO outgoing Permit(...)
            // edges from Terminated, so that is a state-machine-level guarantee, not merely an
            // application-level check.
            var taskTriggerTerminated = await PollUntil(
                async () => (await taskTriggerGrain.GetSnapshot()).PlanItemState == PlanItemState.Terminated,
                TimeSpan.FromSeconds(10));
            taskTriggerTerminated.Should().BeTrue(
                "#179 fix (TaskBehavior half): StageS completing must cascade Exit to TaskTrigger too - Table 8.9 puts Task in the same `<impossible>` column as Stage, not the Milestone/EventListener column that legitimately survives a completed parent");

            (await stageSGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Completed,
                "StageS must still be Completed - the cascade must not have reopened it");
        }

        // Table 8.9's `complete` row only marks {Available, Enabled, Active, Suspended} as
        // impossible alongside a Completed parent - {Disabled, Completed, Terminated, Failed} may
        // coexist. Uses Stage-typed (not Task-typed) children deliberately: StageBehavior's state
        // machine (ConfigureForStageOrTask) permits Exit from Disabled and Failed too, same as
        // Available/Enabled/Active/Suspended, so the fix cannot rely on a bare CanFire(Exit) gate
        // (that's what the pre-existing Exit/Terminate cascade above uses) - it must positively
        // exclude the terminal set, which is exactly what this scenario pins.
        [Fact]
        public async Task StageCompletionCascade__Given_AutoCompleteStageWithDisabledAndFailedChildren__When_ParentCompletes__Then_ChildrenAreLeftAlone()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            var taskRDefinition = new HumanTask { Id = "TaskR", IsBlocking = true };

            var stageDDefinition = new Stage { Id = "StageD", AutoComplete = false };
            var stageFDefinition = new Stage { Id = "StageF", AutoComplete = false };

            var stageSDefinition = new Stage
            {
                Id = "StageS",
                AutoComplete = true,
                PlanItemDefinitions = { taskRDefinition, stageDDefinition, stageFDefinition },
                PlanItems =
                {
                    new Interfaces.Model.PlanItem
                    {
                        Id = "PlanItemR",
                        DefinitionRef = taskRDefinition.Id,
                        ItemControl = new PlanItemControl { RequiredRule = Rules.IsRequiredRule }
                    },
                    new Interfaces.Model.PlanItem { Id = "PlanItemD", DefinitionRef = stageDDefinition.Id },
                    new Interfaces.Model.PlanItem { Id = "PlanItemF", DefinitionRef = stageFDefinition.Id }
                }
            };

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = Scope,
                    PlanItemDefinitions = { stageSDefinition },
                    PlanItems =
                    {
                        new Interfaces.Model.PlanItem { Id = "PlanItemStage", DefinitionRef = stageSDefinition.Id }
                    }
                }
            };

            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .Define(@case);

            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope);
            await caseGrain.Create(caseDefinitionId);
            var caseSnapshot = await caseGrain.Trigger(PlanItemTransition.Create);

            var stageSInstanceId = caseSnapshot.BehaviorExtension.Children["PlanItemStage"].Keys.Single();
            var stageSGrain = _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{stageSInstanceId}");

            var stageSSnapshot = await stageSGrain.Trigger(PlanItemTransition.ManualStart);
            stageSSnapshot.PlanItemState.Should().Be(PlanItemState.Active);

            var stageSChildren = ((StageBehaviorSnapshot)stageSSnapshot.BehaviorExtension).Children;

            var taskRGrain = ResolveChild(caseInstanceId, stageSInstanceId, stageSChildren, "PlanItemR");
            var stageDGrain = ResolveChild(caseInstanceId, stageSInstanceId, stageSChildren, "PlanItemD");
            var stageFGrain = ResolveChild(caseInstanceId, stageSInstanceId, stageSChildren, "PlanItemF");

            // drive StageD to Disabled (Enabled -[Disable]-> Disabled)
            (await stageDGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Enabled);
            var stageDSnapshot = await stageDGrain.Trigger(PlanItemTransition.Disable);
            stageDSnapshot.PlanItemState.Should().Be(PlanItemState.Disabled);

            // drive StageF to Failed (Enabled -[ManualStart]-> Active -[Fault]-> Failed)
            await stageFGrain.Trigger(PlanItemTransition.ManualStart);
            (await stageFGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active);
            var stageFSnapshot = await stageFGrain.Trigger(PlanItemTransition.Fault);
            stageFSnapshot.PlanItemState.Should().Be(PlanItemState.Failed);

            // complete the only required child - no Active children remain (Disabled/Failed are
            // both terminal), so StageS auto-completes
            await taskRGrain.Trigger(PlanItemTransition.ManualStart);
            await taskRGrain.Trigger(PlanItemTransition.Complete);

            var stageSCompleted = await PollUntil(
                async () => (await stageSGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed,
                TimeSpan.FromSeconds(10));
            stageSCompleted.Should().BeTrue("Disabled and Failed are both terminal, and the only required child just completed - autoComplete=true is satisfied");

            // give any (incorrect) cascade a generous window to misfire before asserting it didn't
            var stageDMoved = await PollUntil(
                async () => (await stageDGrain.GetSnapshot()).PlanItemState != PlanItemState.Disabled,
                TimeSpan.FromSeconds(3));
            stageDMoved.Should().BeFalse("Table 8.9's `complete` row permits Disabled to coexist with a Completed parent - it must be left alone, not cascaded to Terminated");

            var stageFMoved = await PollUntil(
                async () => (await stageFGrain.GetSnapshot()).PlanItemState != PlanItemState.Failed,
                TimeSpan.FromSeconds(3));
            stageFMoved.Should().BeFalse("Table 8.9's `complete` row permits Failed to coexist with a Completed parent - it must be left alone, not cascaded to Terminated");
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
