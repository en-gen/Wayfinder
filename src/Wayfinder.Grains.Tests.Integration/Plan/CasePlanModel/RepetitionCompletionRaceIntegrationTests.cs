using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.Case;
using Wayfinder.Grains.Interfaces.Plan.PlanItem;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Orleans;
using Xunit;
using CaseModel = Wayfinder.Grains.Interfaces.Model.Case;

namespace Wayfinder.Grains.Tests.Integration.Plan.CasePlanModel
{
    // Issue #198 - Table 8.12 completion racing a determined-but-unspawned repetition, end to end.
    //
    // The defect: a child's Complete/Terminate transition does two things whose relative order
    // nothing guarantees. It durably records "I have determined a repetition" on its OWN journal
    // (BaseBehavior.TryRepeatOnCompleteOrTerminate: RaiseEvent(new Repeated()) + ConfirmEvents),
    // and it publishes PlanItemRepetitionCriteriaMetEvent to its containing Stage on a stream that
    // is namespaced per event TYPE - i.e. a different stream from the PlanItemTransitionedEvent
    // that drives the container's own Table 8.12 check. The container could therefore complete in
    // the gap, dropping the successor instance entirely (and, before #178 refused the resulting
    // late spawn, materialising Table 8.9's <impossible> Completed-parent/live-child cell).
    //
    // The fix reads the child's LIVE state instead of trying to order the two streams - see
    // StageBehavior.RepetitionRequestsAwaitingResolution. These scenarios pin that reasoning
    // end-to-end, through real grains and real streams:
    //   1. the load-bearing ordering invariant the whole design rests on;
    //   2. the sibling-driven completion path, where the container's check is triggered by a
    //      completely unrelated child and so cannot be fixed by anything that only inspects the
    //      triggering event;
    //   3. the manual refusal and its Warning log - the operator-facing half of the escape hatch;
    //   4. the release path, where resolving the request has to re-open a completion check that
    //      nothing else will ever run again.
    // The deterministic per-branch behaviour of the gate itself lives at the unit layer
    // (StageBehaviorTests_RepetitionCompletionGate.cs), where the interleaving is a fixture rather
    // than a coin flip.
    [Collection(ClusterCollection.Name)]
    public class RepetitionCompletionRaceIntegrationTests
    {
        private const string Scope = "CPM";
        private const string RepeatingTaskDefinitionId = "RepeatingTask";
        private const string RepeatingPlanItemId = "PlanItemRepeating";
        private const string SiblingTaskDefinitionId = "SiblingTask";
        private const string SiblingPlanItemId = "PlanItemSibling";

        private readonly IClusterClient _clusterClient;
        private readonly FakeLoggerProvider _logs;

        public RepetitionCompletionRaceIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;
            _logs = fixture.Logs;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        // THE LOAD-BEARING INVARIANT. #198's fix judges "is a repetition owed?" from the child's
        // own live snapshot rather than from a stream message, which is only sound if a child that
        // is going to repeat can never be observed as terminal BEFORE it has confirmed Repeated.
        //
        // It holds because of Orleans' scheduling, not by luck: PlanItemGrain is not [Reentrant],
        // and BaseBehavior.TryRepeatOnCompleteOrTerminate runs as a Completed/Terminated ENTRY
        // action, i.e. inside the same grain turn as the transition itself. No other call - not
        // this test's GetSnapshot, not the parent Stage's - can be interleaved into that turn, so
        // every observer sees either the pre-transition state or the fully-settled post-transition
        // state, never the intermediate "terminal but not yet Repeated" one.
        //
        // Probed by hammering GetSnapshot concurrently with the Trigger(Complete) that produces
        // the repetition, from outside the grain, and asserting the intermediate state is never
        // observed. This is a REGRESSION guard as much as a proof: reordering
        // TryRepeatOnCompleteOrTerminate out of the transition turn (e.g. onto a timer, a
        // fire-and-forget task, or a separate grain call) would silently reopen #198, and this is
        // the scenario that would catch it.
        [Fact]
        public async Task RepeatingChild__Given_ObservedConcurrentlyWithItsOwnCompletion__Then_NeverTerminalWithoutRepeated()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseGrain = await CreateCase(caseInstanceId, withSibling: false, manuallyActivated: true);

            var repeatingGrain = await ResolveInstance(caseGrain, RepeatingPlanItemId, repetition: 0);
            await repeatingGrain.Trigger(PlanItemTransition.ManualStart);

            var observations = new List<PlanItemSnapshot>();

            var complete = repeatingGrain.Trigger(PlanItemTransition.Complete);
            while (!complete.IsCompleted)
            {
                observations.Add(await repeatingGrain.GetSnapshot());
            }
            await complete;

            observations.Add(await repeatingGrain.GetSnapshot());

            observations.Should().NotBeEmpty("the probe must actually have read the child at least once");

            observations
                .Where(o => o.PlanItemState.IsTerminal())
                .Should().OnlyContain(o => o.Repeated,
                    "#198's blocking predicate reads Repeated off a terminal child's live snapshot - " +
                    "if a repeating child were ever observable as terminal before confirming Repeated, " +
                    "the containing Stage could still complete over the repetition it owes (8.6.4 / Table 8.9)");

            observations.Last().PlanItemState.Should().Be(PlanItemState.Completed);
            observations.Last().Repeated.Should().BeTrue();
        }

        // THE SIBLING PATH. The repeating child completes first, determining a repetition whose
        // request is still in flight; then a NON-repeating sibling completes, and it is the
        // sibling's transition that drives the container's Table 8.12 check. Nothing in that
        // triggering event mentions the repetition at all - so this scenario is unreachable for any
        // fix that tries to correlate the completion check with the repetition message, and is
        // exactly what evaluating live state over EVERY child on EVERY completion path is for.
        //
        // The CasePlanModel here leaves autoComplete at its default FALSE with no PlanningTable, so
        // Table 8.12's Branch 1 ("no Active children AND all children terminal AND no
        // DiscretionaryItems") becomes satisfiable the instant the sibling completes. This scenario
        // fails 6 runs in 8 with the gate neutralised and passes deterministically with it in
        // place; the single-child shape the three formerly-quarantined #198 scenarios use was
        // measured at 5 in 10 on this branch's parent commit. It stays probabilistic in the FAILING
        // direction only - a fix that is present cannot lose this race, so a green run here is not
        // luck even though a red one before the fix was.
        [Fact]
        public async Task SiblingCompletion__Given_RepeatingChildStillOwedASuccessor__Then_CaseWaitsAndTheRepetitionSpawns()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseGrain = await CreateCase(caseInstanceId, withSibling: true, manuallyActivated: false);

            var repeatingGrain = await ResolveInstance(caseGrain, RepeatingPlanItemId, repetition: 0);
            var siblingGrain = await ResolveInstance(caseGrain, SiblingPlanItemId, repetition: 0);

            (await repeatingGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active);
            (await siblingGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active);

            // The repeating child determines its successor...
            await repeatingGrain.Trigger(PlanItemTransition.Complete);
            (await repeatingGrain.GetSnapshot()).Repeated.Should().BeTrue(
                "8.6.4: completing a no-entry-criteria item whose RepetitionRule re-evaluates TRUE " +
                "must durably record the determination before this call returns");

            // ...and then the SIBLING's completion is what drives the container's Table 8.12
            // check, with the repetition request very likely still undelivered.
            await siblingGrain.Trigger(PlanItemTransition.Complete);

            var repetitionSpawned = await PollUntil(async () =>
                await ResolveInstance(caseGrain, RepeatingPlanItemId, repetition: 1) != null);

            repetitionSpawned.Should().BeTrue(
                "8.6.4: the successor the repeating child determined must actually be created - " +
                "if the CasePlanModel completed first, #178 correctly refuses the late spawn and it " +
                "is lost forever (#198)");

            var caseSnapshot = await caseGrain.GetSnapshot();
            caseSnapshot.PlanItemState.Should().NotBe(PlanItemState.Completed,
                "Table 8.9's complete rows make a Completed parent with a live child <impossible>; " +
                "the freshly spawned repetition is Active, so the CasePlanModel must still be running");
        }

        // THE MANUAL REFUSAL, and the Warning that makes it diagnosable. #198's escape hatch has
        // two halves: the refusal names the blocking child instance ids to whoever called
        // Trigger(Complete), and it says the same thing at Warning to whoever is reading logs. Only
        // the second half survives to an operator investigating a case that quietly stopped moving,
        // so it is asserted here through the REAL DI-resolved logger the silo hands the grain
        // (#194's FakeLoggerProvider, wired into ClusterFixture for exactly this).
        //
        // Why this is not a coin flip like the two scenarios above, even though it rides the same
        // in-flight repetition request: the completion check here is driven by a DIRECT grain call
        // from this test - sub-millisecond, no stream hop - racing a memory-stream delivery that
        // cannot arrive sooner than the pulling agent's next cycle (~100ms). The sibling scenario
        // above is a coin flip precisely because BOTH of its signals are stream deliveries.
        //
        // The non-repeating sibling is deliberately left ENABLED for the whole scenario, and that
        // choice is load-bearing in both directions. Enabled is not terminal, so Table 8.12's
        // AUTOMATIC branch ("all children terminal") can never fire and complete the case out from
        // under this test; Enabled is also not Active, so it does not trip the Case lifecycle's own
        // "no Active children" conjunct (Table 8.5/8.6, CasePlanModelBehavior.
        // ManualCompletionCriteriaSatisfied - the Case root keeps that conjunct where an ordinary
        // Stage's manual branch drops it). Anything that refuses below is therefore the #198 gate
        // and nothing else.
        [Fact]
        public async Task ManualComplete__Given_RepeatingChildStillOwedASuccessor__Then_RefusedNamingTheChildAndLoggedAtWarning()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseGrain = await CreateCase(caseInstanceId, withSibling: true, manuallyActivated: true);

            var repeatingInstanceId = await ResolveInstanceId(caseGrain, RepeatingPlanItemId, repetition: 0);
            var repeatingGrain = await ResolveInstance(caseGrain, RepeatingPlanItemId, repetition: 0);
            var siblingGrain = await ResolveInstance(caseGrain, SiblingPlanItemId, repetition: 0);

            (await siblingGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Enabled,
                "the sibling is never started - it holds the automatic branch open without tripping the Case's own no-Active-children conjunct");

            await repeatingGrain.Trigger(PlanItemTransition.ManualStart);

            // Cleared immediately before the act, not in a constructor: this provider lives for the
            // whole TestCluster fixture and every test in this collection writes into it.
            _logs.Clear();

            await repeatingGrain.Trigger(PlanItemTransition.Complete);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => caseGrain.Trigger(PlanItemTransition.Complete));

            ex.Message.Should().Contain(repeatingInstanceId,
                "the refusal must name the specific child instance that is owed a successor, not just say the criteria were not met");

            _logs.Entries.Should().Contain(
                e => e.Level == LogLevel.Warning && e.Message.Contains(repeatingInstanceId),
                "a held completion must be explainable from logs alone - that is the whole escape hatch (#194/#198)");
        }

        // THE RELEASE PATH. The gate above is only half a fix: something has to re-evaluate Table
        // 8.12 once the repetition request it was holding for finally arrives. Table 8.12 is
        // otherwise evaluated EXCLUSIVELY from a child's terminal PlanItemTransitionedEvent
        // (StageBehavior.HandleChildTransitioned), and the successor a repetition spawns need not
        // produce one: with ManualActivationRule TRUE (8.6.2/Table 5.51's absence default) it lands
        // Enabled and simply sits there. So in this shape - autoComplete=TRUE container, a
        // NON-required repeating child that finishes BEFORE the last required child - the container
        // would satisfy Table 8.12's autoComplete=TRUE column in full (no Active children, the only
        // required child Completed, the successor merely Enabled) and still never complete, because
        // the check that would have noticed already ran and was held by the gate.
        //
        // Measured on this branch (F1): 22 runs of exactly this scenario against the gate-only
        // commit completed 14 times and stalled 8 (~36%), while the repetition itself was preserved
        // 22/22; with the release path in place, 25/25 completed. The stall is a coin flip, so a
        // single green run is not proof on its own - but it is the SAME coin flip #198's own
        // scenarios ride, and the failing direction is what this pins.
        [Fact]
        public async Task AutoCompleteContainer__Given_ARepetitionResolvesAfterTheLastRequiredChild__Then_TheContainerStillCompletes()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseGrain = await CreateAutoCompleteCaseWithRequiredSibling(caseInstanceId);

            var repeatingGrain = await ResolveInstance(caseGrain, RepeatingPlanItemId, repetition: 0);
            var siblingGrain = await ResolveInstance(caseGrain, SiblingPlanItemId, repetition: 0);

            (await repeatingGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Enabled,
                "the repeating item is manually activated, so its successor will land Enabled - never Active, never terminal, i.e. never a trigger for another completion check");
            (await siblingGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Active);

            // The non-required repeating child finishes FIRST, determining a successor. The
            // container cannot complete yet: the required sibling is still Active.
            await repeatingGrain.Trigger(PlanItemTransition.ManualStart);
            await repeatingGrain.Trigger(PlanItemTransition.Complete);
            (await repeatingGrain.GetSnapshot()).Repeated.Should().BeTrue();

            // ...and now the LAST required child completes, satisfying Table 8.12's
            // autoComplete=TRUE column outright - very likely while the repetition request is still
            // in flight, so the gate holds this check.
            await siblingGrain.Trigger(PlanItemTransition.Complete);

            var repetitionSpawned = await PollUntil(async () =>
                await ResolveInstance(caseGrain, RepeatingPlanItemId, repetition: 1) != null);
            repetitionSpawned.Should().BeTrue(
                "the gate must still do its #198 job here - the successor the repeating child determined has to exist");

            var completed = await PollUntil(async () =>
                (await caseGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed);

            // Deliberately NOT asserting that the successor is still Enabled at this point: it
            // lands Enabled (8.6.2/Table 5.51 - that is what makes this shape produce no further
            // transition, and rep 0's own Enabled assertion above already pins the rule), but the
            // container completing cascades exit to it, so reading it back afterwards is a race
            // against a cascade this scenario is not about.
            completed.Should().BeTrue(
                "Table 8.12's autoComplete=TRUE column (no Active children, all required children terminal) is satisfied " +
                "and the repetition the gate was holding for has been spawned - resolving the request must re-open the " +
                "completion check the gate refused, or the container stalls Active forever. Case is " +
                $"{(await caseGrain.GetSnapshot()).PlanItemState}, repetition 1 is " +
                $"{(await (await ResolveInstance(caseGrain, RepeatingPlanItemId, repetition: 1)).GetSnapshot()).PlanItemState}");
        }

        // The F1 shape (see the scenario above): autoComplete=TRUE, a non-required MANUALLY
        // ACTIVATED repeating child (so its successor lands Enabled and never transitions again),
        // and a REQUIRED, automatically-activated sibling that is Active from creation and supplies
        // the last terminal transition.
        private async Task<ICaseGrain> CreateAutoCompleteCaseWithRequiredSibling(Guid caseInstanceId)
        {
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            var repeatingTask = new HumanTask { Id = RepeatingTaskDefinitionId, IsBlocking = true };
            var siblingTask = new HumanTask { Id = SiblingTaskDefinitionId, IsBlocking = true };

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = Scope,
                    AutoComplete = true,
                    PlanItemDefinitions = { repeatingTask, siblingTask },
                    PlanItems =
                    {
                        new Interfaces.Model.PlanItem
                        {
                            Id = RepeatingPlanItemId,
                            DefinitionRef = repeatingTask.Id,
                            ItemControl = new PlanItemControl
                            {
                                RepetitionRule = Rules.IsRepeatableRule,
                                ManualActivationRule = Rules.IsManuallyActivated
                                // No RequiredRule - Table 5.51's absence default is FALSE. Table
                                // 8.12's autoComplete=TRUE column therefore never waits on this
                                // item's successor at all; only the #198 gate does.
                            }
                        },
                        new Interfaces.Model.PlanItem
                        {
                            Id = SiblingPlanItemId,
                            DefinitionRef = siblingTask.Id,
                            ItemControl = new PlanItemControl
                            {
                                RequiredRule = Rules.IsRequiredRule,
                                ManualActivationRule = Rules.NotManuallyActivated
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

        private async Task<ICaseGrain> CreateCase(Guid caseInstanceId, bool withSibling, bool manuallyActivated)
        {
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            var repeatingTask = new HumanTask { Id = RepeatingTaskDefinitionId, IsBlocking = true };
            var siblingTask = new HumanTask { Id = SiblingTaskDefinitionId, IsBlocking = true };

            var casePlanModel = new Stage
            {
                Id = Scope,
                PlanItemDefinitions = { repeatingTask },
                PlanItems =
                {
                    new Interfaces.Model.PlanItem
                    {
                        Id = RepeatingPlanItemId,
                        DefinitionRef = repeatingTask.Id,
                        ItemControl = new PlanItemControl
                        {
                            RepetitionRule = Rules.IsRepeatableRule,
                            // 8.6.2/Table 5.51: absent means TRUE (wait Enabled for a Case worker).
                            // Spelled out either way so each scenario's arrange phase is explicit
                            // about whether it drives the task by hand.
                            ManualActivationRule = manuallyActivated
                                ? Rules.IsManuallyActivated
                                : Rules.NotManuallyActivated
                        }
                    }
                }
            };

            if (withSibling)
            {
                casePlanModel.PlanItemDefinitions.Add(siblingTask);
                casePlanModel.PlanItems.Add(new Interfaces.Model.PlanItem
                {
                    Id = SiblingPlanItemId,
                    DefinitionRef = siblingTask.Id,
                    // No RepetitionRule: this child owes nothing, it only supplies the terminal
                    // transition that drives the container's completion check.
                    ItemControl = new PlanItemControl
                    {
                        ManualActivationRule = manuallyActivated
                            ? Rules.IsManuallyActivated
                            : Rules.NotManuallyActivated
                    }
                });
            }

            var @case = new CaseModel
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = casePlanModel
            };

            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .Define(@case);

            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope);
            await caseGrain.Create(caseDefinitionId);
            await caseGrain.Trigger(PlanItemTransition.Create);

            return caseGrain;
        }

        private async Task<string> ResolveInstanceId(ICaseGrain caseGrain, string planItemId, int repetition)
        {
            var snapshot = await caseGrain.GetSnapshot();

            return snapshot.BehaviorExtension.Children[planItemId]
                .Where(kvp => kvp.Value == repetition)
                .Select(kvp => kvp.Key)
                .Single();
        }

        private async Task<IPlanItemInternalGrain> ResolveInstance(ICaseGrain caseGrain, string planItemId, int repetition)
        {
            var snapshot = await caseGrain.GetSnapshot();

            if (snapshot.BehaviorExtension?.Children.TryGetValue(planItemId, out var instances) != true) return null;

            var instanceId = instances
                .Where(kvp => kvp.Value == repetition)
                .Select(kvp => kvp.Key)
                .SingleOrDefault();

            return instanceId == null
                ? null
                : _clusterClient.GetGrain<IPlanItemInternalGrain>(caseGrain.GetPrimaryKey(), $"{Scope}.{instanceId}");
        }

        // Positive poll (#174 discipline): fails fast the instant the condition holds, and only
        // spends the full budget when it never does. 10s is generous against the memory stream
        // provider's ~100ms agent cadence while staying cheap on this deliberately-serialized
        // shared cluster (#153/#154).
        private static async Task<bool> PollUntil(Func<Task<bool>> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (await condition()) return true;
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }

            return await condition();
        }
    }
}
