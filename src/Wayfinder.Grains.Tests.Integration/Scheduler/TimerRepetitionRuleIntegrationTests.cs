using System;
using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.Case;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Tests.Integration.Plan.CasePlanModel.RepetitionGuard;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using NodaTime;
using Orleans;
using Orleans.Streams;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Scheduler
{
    // #182 (sub-claim 1) fix - graduated from Scheduler/Repro/Issue182_TimerRepetitionCeilingIntegrationTests.cs
    // (repro history: CONFIRMED 3/3, see #182's comment thread). That repro test proved the DEFECT
    // by asserting the case reached Failed; this is its post-fix replacement, asserting the CORRECT
    // behavior instead - see this task's own determination on (a1) vs (a2) below.
    //
    // Determination (recorded here for anyone reading this test cold): EventListeners have no
    // RepetitionRule concept per 5.4.11.3 - BaseBehavior.TryRepeatOnCompleteOrTerminate's own
    // remarks and this repro's original evidence both cite it, and a recurring TimerEventListener
    // genuinely repeats via its own TimerExpression (6.4.10), not a RepetitionRule re-evaluation -
    // so (a1) "EventListeners DO repeat per spec, consult the rule" is spec-incorrect, and the FIX
    // is NOT "make timers spec-conformant repetition". (a2) "the republish is itself unsanctioned"
    // is also not quite right either: the unconditional republish is exactly how a recurring timer
    // is SUPPOSED to work absent any rule. The actual defect is narrower - the engine's OWN #67
    // safety valve (RepetitionGuardOptions.MaxRepetitionsPerPlanItem; see
    // RepetitionCeilingExceeded's remarks) applies uniformly to every repeating plan item with no
    // way to exempt a genuinely-unbounded, spec-legal recurring timer, so a legitimate schedule
    // inevitably faults its container. The fix (BaseBehavior.HasExplicitRepetitionRule,
    // TimerEventListenerBehavior.ProcessTick's Completed branch) is a conservative, ENGINE-LEVEL
    // escape hatch: if a case author attaches an ItemControl.RepetitionRule to a timer anyway
    // (nothing in the schema forbids it, even though the spec gives it no meaning there), an
    // explicitly-false evaluation stops the republish; no attached rule (the common case) leaves
    // the timer's repetition exactly as unconditional as before this fix - see this fix's own
    // report for the explicit confirmation that the NO-RULE case is intentionally left unchanged
    // (still climbs to the ceiling and faults) pending a separate maintainer decision on whether
    // TimerEventListener repetition should count against the plan-item ceiling at all.
    //
    // Test design note (post-review): the original version of this test waited on an INDEPENDENT
    // stream observer to witness real Quartz-fired ticks (150ms interval) as its positive
    // synchronization point. That measured 0/10 ticks, deterministically, on both full-suite runs -
    // not a flake, but real-clock starvation: under full-suite load this suite's own real-time
    // Quartz scheduling can be denied a thread for the whole observation window, no matter how wide
    // the budget. Since the fix under test is about what TimerEventListenerBehavior.ProcessTick
    // does when a tick ARRIVES (the RepetitionRule guard), not about whether Quartz manages to fire
    // one under load, this version drives the tick path directly - publishing synthetic
    // TimerTickedEvent messages onto the exact stream identity TimerTickJob publishes to
    // (StreamProviderExtensions.GetCaseEventStream, confirmed against TimerTickJob.cs) - so the
    // assertion is grounded in this fix's own logic, not in Quartz's real-time scheduling fidelity.
    // The timer's OWN schedule is pushed a full hour out so the real Quartz trigger genuinely never
    // fires during the test, eliminating any residual race between real and synthetic ticks.
    [Collection(RepetitionGuardClusterCollection.Name)]
    public class TimerRepetitionRuleIntegrationTests
    {
        private const string Scope = "CPM";
        private const string TimerDefinitionId = "RecurringTimer";
        private const string TimerPlanItemId = "PlanItemTimer";
        private const string SentinelDefinitionId = "SentinelTask";
        private const string SentinelPlanItemId = "PlanItemSentinel";
        private const int SimulatedExtraTicks = 9;

        private readonly IClusterClient _clusterClient;

        public TimerRepetitionRuleIntegrationTests(RepetitionGuardClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000005");
        }

        [Fact]
        public async Task RecurringTimer__Given_ExplicitFalseRepetitionRule__When_ManyTicksArrive__Then_ExactlyOneInstanceEverExistsAndCaseNeverFaults()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            // A real but genuinely-never-fires-during-this-test schedule (single shot, one hour
            // out) - the plan item still needs a valid TimerExpression to reach Available and
            // schedule a real Quartz trigger the normal way, but this test asserts entirely on
            // SYNTHETIC ticks published directly below, so the real trigger must stay silent for
            // the whole run or it would spuriously add an extra, untracked live tick.
            var timerDefinition = new TimerEventListener
            {
                Id = TimerDefinitionId,
                TimerExpression = Timers.TimerExpression(SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromHours(1)))
            };

            // Sentinel sibling (blocking HumanTask, no ItemControl -> ManualActivationRule defaults
            // TRUE -> permanently Enabled): keeps the CasePlanModel's autoComplete=FALSE Branch 1
            // from completing the case once the timer's single instance reaches Completed - same
            // rationale as the original repro and RepetitionGuardFootgunIntegrationTests' sentinel.
            var sentinelDefinition = new HumanTask { Id = SentinelDefinitionId, IsBlocking = true };

            var @case = new Interfaces.Model.Case
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = Scope,
                    PlanItemDefinitions = { timerDefinition, sentinelDefinition },
                    PlanItems =
                    {
                        new Interfaces.Model.PlanItem
                        {
                            Id = TimerPlanItemId,
                            DefinitionRef = timerDefinition.Id,
                            // THE fix under test: an explicitly-false RepetitionRule, attached the
                            // same way a Task/Stage's would be.
                            ItemControl = new PlanItemControl { RepetitionRule = Rules.NotRepeatableRule }
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
            var caseSnapshot = await caseGrain.Trigger(PlanItemTransition.Create);

            var firstTimerInstanceId = caseSnapshot.BehaviorExtension.Children[TimerPlanItemId].Keys.Single();

            var tickStream = _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<TimerTickedEvent>(caseInstanceId, firstTimerInstanceId);

            // Drive the FIRST occurrence directly - same stream identity and event shape
            // TimerTickJob.Execute publishes in production (confirmed against
            // Scheduler/TimerTickJob.cs), so this exercises the exact same
            // TimerEventListenerBehavior.HandleTimerTickedEvent subscription path a real Quartz
            // tick would, with no dependency on Quartz's own thread pool actually getting
            // scheduled under load.
            await tickStream.OnNextAsync(new TimerTickedEvent(null, null, DateTimeOffset.UtcNow, null));

            var timerGrain = _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{firstTimerInstanceId}");

            // Positive, decisive sync point: poll for the FIRST synthetic tick's Occur transition
            // to land. This is not subject to real-clock starvation - the event is already
            // published and durably queued for delivery, so it WILL be processed as soon as the
            // grain gets a turn; the budget only protects against a slow/contended runner, it is
            // never waiting on an uncertain external clock to fire something in the first place.
            var reachedCompleted = await PollUntil(
                async () => (await timerGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed,
                TimeSpan.FromSeconds(30));
            reachedCompleted.Should().BeTrue("the first synthetic tick must drive Available -> Occur -> Completed before any repetition logic is even reachable");

            // Now drive SimulatedExtraTicks more synthetic ticks - each one simulating what a real
            // recurring schedule would have delivered next. Every one of these hits
            // TimerEventListenerBehavior.ProcessTick's Completed branch, which is exactly the
            // guard under test (BaseBehavior.HasExplicitRepetitionRule + EvaluateRepetitionRule).
            // Published sequentially and awaited so each publish call only returns once the
            // stream provider has accepted it for delivery.
            for (var i = 0; i < SimulatedExtraTicks; i++)
            {
                await tickStream.OnNextAsync(new TimerTickedEvent(null, null, DateTimeOffset.UtcNow, null));
            }

            // Settle-and-sample: unlike waiting for an uncertain external tick, these
            // SimulatedExtraTicks are already durably published and guaranteed to be delivered
            // eventually - there is nothing further that could arrive later that hasn't already
            // been fed. Sampling repeatedly over a short window (matching
            // RepetitionGuardFootgunIntegrationTests' own established "absence half" convention,
            // widened here to cover several already-queued deliveries draining) confirms the guard
            // holds steady, not just at one instant.
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));

                var sampledSnapshot = await caseGrain.GetSnapshot();
                sampledSnapshot.PlanItemState.Should().Be(PlanItemState.Active,
                    "the case must never fault while draining the already-published extra ticks - " +
                    "the explicit false RepetitionRule must be honored on every one of them, not just the first");
                sampledSnapshot.BehaviorExtension.Children[TimerPlanItemId].Should().HaveCount(1,
                    "no additional timer instance may appear while draining the already-published extra ticks");
            }

            var finalSnapshot = await caseGrain.GetSnapshot();

            finalSnapshot.PlanItemState.Should().Be(PlanItemState.Active,
                "the case must never fault - the explicit false RepetitionRule must have stopped " +
                "the timer from respawning on every one of the extra ticks, long before the engine's " +
                "own repetition ceiling (5, via RepetitionGuardClusterFixture) could ever be reached");

            var timerInstances = finalSnapshot.BehaviorExtension.Children[TimerPlanItemId];
            timerInstances.Should().HaveCount(1,
                $"exactly one timer instance must ever have existed across all {SimulatedExtraTicks + 1} " +
                "delivered ticks - the explicitly-false RepetitionRule must be consulted and honored on " +
                "every Completed-branch tick, not just the first, or a second instance would have spawned");
            timerInstances.Should().ContainKey(firstTimerInstanceId,
                "the single surviving instance must be the original one - no respawn ever occurred");
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
