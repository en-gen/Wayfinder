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
using NodaTime.Text;
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
    // the timer's repetition exactly as unconditional as before this fix.
    [Collection(RepetitionGuardClusterCollection.Name)]
    public class TimerRepetitionRuleIntegrationTests
    {
        private const string Scope = "CPM";
        private const string TimerDefinitionId = "RecurringTimer";
        private const string TimerPlanItemId = "PlanItemTimer";
        private const string SentinelDefinitionId = "SentinelTask";
        private const string SentinelPlanItemId = "PlanItemSentinel";

        private readonly IClusterClient _clusterClient;

        public TimerRepetitionRuleIntegrationTests(RepetitionGuardClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000005");
        }

        [Fact]
        public async Task RecurringTimer__Given_UnboundedScheduleAndExplicitFalseRepetitionRule__When_TicksContinue__Then_ExactlyOneInstanceEverExistsAndCaseNeverFaults()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            // Same unbounded, genuinely-recurring schedule shape as the original repro (150ms
            // interval, bare "R" - no repetitions cap) run against the SAME artificially low
            // ceiling fixture (RepetitionGuardClusterFixture.LowCeiling = 5) - proving the fix
            // holds even under an adversarially tight budget, not merely under production's
            // 10,000 default where "never faulted" could just mean "hasn't gotten there yet".
            var isoPeriod = PeriodPattern.NormalizingIso.Format(Period.FromMilliseconds(150).Normalize());
            var timerDefinition = new TimerEventListener
            {
                Id = TimerDefinitionId,
                TimerExpression = new Expression
                {
                    Id = Guid.NewGuid().ToString(),
                    Language = ExpressionLanguage.Jint,
                    Body = $"'R/{isoPeriod}'"
                }
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

            // Independent observer on the FIRST (and, per this fix, ONLY) timer instance's own
            // tick stream - same established technique as Issue182_SuspendedTimerTickDroppedIntegrationTests:
            // decoupled from the plan item's own (now-unsubscribed-after-Completed) behavior, so it
            // keeps witnessing every real Quartz tick regardless of what the grain-side subscription
            // does. Used here as the POSITIVE synchronization point (issue #174 compliance): rather
            // than sleeping a fixed window and hoping nothing spawned, we wait for PROOF that many
            // more real ticks landed after the first occurrence, then assert the spawn count against
            // that decisive backdrop - "no runaway spawn" becomes a grounded claim, not an absence
            // inferred from silence.
            var observedTicks = 0;
            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<TimerTickedEvent>(caseInstanceId, firstTimerInstanceId)
                .SubscribeAsync((@event, token) =>
                {
                    observedTicks++;
                    return Task.CompletedTask;
                });

            // Positive poll: at least 10 real ticks witnessed (the first occurrence plus >=9 more
            // that would have marched straight past the LowCeiling=5 ceiling pre-fix). 150ms
            // interval means this settles in ~1.5s in isolation; the 30s budget (matching this
            // suite's own established convention for this exact timer area - see
            // RepetitionGuardFootgunIntegrationTests and the original repro this test replaced)
            // protects against real cross-cluster Quartz contention when the full suite runs many
            // real-clock TestClusters in parallel (issue #153) - confirmed necessary: a tighter
            // 15s budget measured 0/10 ticks once under full-suite contention even though the
            // schedule is proven to settle in ~1.5s isolated 3/3.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (observedTicks < 10 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }

            observedTicks.Should().BeGreaterThanOrEqualTo(10,
                "the independent observer must witness many more real ticks than the ceiling (5) - " +
                "without this positive signal we cannot tell 'the schedule stopped ticking' from " +
                "'the engine correctly stopped spawning despite ticks continuing'");

            var finalSnapshot = await caseGrain.GetSnapshot();

            finalSnapshot.PlanItemState.Should().Be(PlanItemState.Active,
                "the case must never fault - the explicit false RepetitionRule must have stopped " +
                "the timer from respawning long before the engine's own repetition ceiling could " +
                "ever be reached, even though the underlying schedule kept ticking well past it");

            var timerInstances = finalSnapshot.BehaviorExtension.Children[TimerPlanItemId];
            timerInstances.Should().HaveCount(1,
                "exactly one timer instance must ever have existed - the explicitly-false " +
                "RepetitionRule must be consulted and honored on every Completed-branch tick, not " +
                "just the first, or a second instance would have spawned");
            timerInstances.Should().ContainKey(firstTimerInstanceId,
                "the single surviving instance must be the original one - no respawn ever occurred");
        }
    }
}
