using System;
using System.Linq;
using System.Threading.Tasks;
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
    // D7 - 8.6.4 RepetitionRule / 5.4.11.3, both halves, end-to-end through real grains,
    // model-driven (ICaseGrain.Create + Trigger only).
    //
    // Half 1 (first-evaluation discard): "The first time a Milestone, Stage, or Task instance is
    // instantiated and transitions to the Available state it is not considered a repetition,
    // nevertheless the RepetitionRule MUST be evaluated and its result discarded." Observable on
    // the snapshot: a task whose RepetitionRule is constant-TRUE must still read Repeatable=false
    // right after instantiation - the truthy first evaluation was evaluated, then discarded.
    //
    // Half 2 (re-evaluation on complete/terminate for no-entry-criteria items): "Stage and Task
    // instances with a RepetitionRule that do not have any entry criteria, will try to create a
    // new instance every time an instance transitions into the Complete or Terminate state."
    // Observable end-to-end: completing rep 0 must spawn rep 1 under the same parent - the full
    // chain is TaskBehavior's Completed entry action re-evaluating the rule, publishing
    // PlanItemRepetitionCriteriaMetEvent, the parent CasePlanModelBehavior's HandleChildRepeated
    // receiving it, and CreateChild instantiating repetition 1.
    //
    // Case shape:
    //   Case "CPM" (outermost Stage)
    //     PlanItemDefinitions: TaskA (blocking HumanTask)
    //     PlanItems: PlanItemTask -> TaskA, NO entry criteria,
    //                ItemControl.RepetitionRule = Rules.IsRepeatableRule (or NotRepeatableRule)
    [Collection(ClusterCollection.Name)]
    public class RepetitionOnCompletionIntegrationTests
    {
        private const string Scope = "CPM";
        private const string TaskDefinitionId = "TaskA";
        private const string TaskPlanItemId = "PlanItemTask";

        private readonly IClusterClient _clusterClient;

        public RepetitionOnCompletionIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        // #198 (found running the #178 verification suite at scale - not part of the original D7
        // scenario) - this test's own case shape was EXACTLY the #198 race's shape: a single
        // no-entry-criteria repeating child under a CasePlanModel that defaults AutoComplete to
        // FALSE (never set here). Completing rep 0 used to both (1) re-evaluate its RepetitionRule
        // and publish PlanItemRepetitionCriteriaMetEvent and (2) trigger the parent's Table 8.12
        // completion check on two separate, unordered streams, letting the CasePlanModel
        // legitimately complete before rep 1's repetition request was delivered. Fixed by #198:
        // BaseBehavior.HandleTransitioned now decides the RepetitionRule re-evaluation BEFORE
        // publishing rep 0's own PlanItemTransitionedEvent and embeds the verdict on it
        // (WillRepeat), so StageBehavior.EvaluateStageCompletionCriteria defers the completion
        // check until rep 1 actually exists instead of racing a second stream to find out.
        [Fact]
        public async Task TaskComplete__Given_NoEntryCriteriaRepeatableTask__Then_FirstEvalDiscardedAndRepetitionSpawnedOnComplete()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseGrain = await CreateCase(caseInstanceId, Rules.IsRepeatableRule);

            var rep0Grain = await FindRepetitionGrain(caseInstanceId, TaskPlanItemId, repetition: 0);
            rep0Grain.Should().NotBeNull("TaskA(rep 0) must have been instantiated by Case creation");

            var rep0Snapshot = await rep0Grain.GetSnapshot();
            rep0Snapshot.PlanItemState.Should().Be(PlanItemState.Enabled,
                "a blocking HumanTask with no entry criteria and no ManualActivationRule waits Enabled (8.6.2/Table 5.51 absence default TRUE)");

            // D7 HALF 1 - the sharp assertion: the RepetitionRule is constant-TRUE, so if the
            // first (Create -> Available) evaluation were persisted rather than discarded,
            // Repeatable would read TRUE here.
            rep0Snapshot.Repeatable.Should().BeFalse(
                "8.6.4: the first evaluation on instantiation MUST be evaluated and its result DISCARDED - a truthy rule must not surface as Repeatable yet");
            rep0Snapshot.Repeated.Should().BeFalse();

            // drive rep 0 to Completed
            await rep0Grain.Trigger(PlanItemTransition.ManualStart);
            await rep0Grain.Trigger(PlanItemTransition.Complete);

            // rep 0's own bookkeeping FIRST - the earliest link in the chain: the complete-time
            // re-evaluation is a REAL evaluation whose result IS persisted (only the
            // instantiation-time one is discarded), and the attempt marks the instance Repeated.
            // If these fail, the child-side entry action never ran/published; if these pass but
            // the rep-1 poll below fails, the break is in delivery to the parent.
            var rep0After = await rep0Grain.GetSnapshot();
            rep0After.PlanItemState.Should().Be(PlanItemState.Completed);
            rep0After.Repeatable.Should().BeTrue(
                "the re-evaluation on Complete is a real, persistable determination - only the instantiation-time evaluation is discarded");
            rep0After.Repeated.Should().BeTrue("the TRUE re-evaluation on Complete must have published the repetition attempt");

            // D7 HALF 2 - completing the no-entry-criteria instance re-evaluates the rule (TRUE)
            // and must spawn repetition 1 under the parent CasePlanModel.
            var rep1Grain = await PollUntilGrainFound(caseInstanceId, TaskPlanItemId, repetition: 1, TimeSpan.FromSeconds(30));
            rep1Grain.Should().NotBeNull(
                "8.6.4: a no-entry-criteria item whose RepetitionRule re-evaluates TRUE on Complete must produce a new instance");

            var rep1Snapshot = await rep1Grain.GetSnapshot();
            rep1Snapshot.Repetition.Should().Be(1);
            rep1Snapshot.PlanItemState.Should().Be(PlanItemState.Enabled,
                "the repetition is a fresh instance and follows the same Available -> Enabled route as the original");
            rep1Snapshot.Repeatable.Should().BeFalse(
                "the repetition's own first evaluation is equally discarded (8.6.4 applies to every instantiation)");

            // #198 - Table 8.9's complete rows: a Completed parent may never coexist with a
            // Stage/Task child in Available/Enabled/Active/Suspended. rep1 is exactly such a
            // child (Enabled, asserted above), so the owning CasePlanModel must still be Active -
            // this is the assertion the original #198 quarantine flagged as missing: the test used
            // to check only that a second instance existed, never that the container's own state
            // was still consistent with that instance's existence.
            (await caseGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.9: the parent must not have completed over the live repetition instance (<impossible> cell)");
        }

        [Fact]
        public async Task TaskComplete__Given_NoEntryCriteriaNonRepeatableTask__Then_NoRepetitionSpawned()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseGrain = await CreateCase(caseInstanceId, Rules.NotRepeatableRule);

            var rep0Grain = await FindRepetitionGrain(caseInstanceId, TaskPlanItemId, repetition: 0);
            rep0Grain.Should().NotBeNull();

            await rep0Grain.Trigger(PlanItemTransition.ManualStart);
            await rep0Grain.Trigger(PlanItemTransition.Complete);

            // ADO #174 - Repeated needs no wait at all (not even the scaled window below):
            // BaseBehavior.HandleTransitioned (see #198's EvaluateRepetitionOnTerminalTransition)
            // decides and acts on the RepetitionRule re-evaluation as part of the Complete
            // transition itself, and Trigger() awaits Stateless's entire FireAsync before
            // returning - so whether Repeated gets raised has already happened, synchronously, by
            // the time this Trigger call above returns (see the sibling flagship test's identical
            // no-wait assertion of rep0After.Repeatable/Repeated right after its own
            // Trigger(Complete)).
            (await rep0Grain.GetSnapshot()).Repeated.Should().BeFalse();

            // absence assertion: allow the (hypothetical) repetition event time to propagate,
            // then confirm nothing spawned
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            var caseSnapshot = await caseGrain.GetSnapshot();
            caseSnapshot.BehaviorExtension.Children[TaskPlanItemId].Should().HaveCount(1,
                "a FALSE re-evaluation on Complete must not spawn a repetition");
        }

        private async Task<ICaseGrain> CreateCase(Guid caseInstanceId, RepetitionRule repetitionRule)
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
                                RepetitionRule = repetitionRule
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
    }
}
