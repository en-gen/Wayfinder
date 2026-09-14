using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Scheduler;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using NodaTime;
using NodaTime.Text;
using Orleans;
using Orleans.Streams;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Scheduler
{
    // ============================================================================================
    // PINS KNOWN-WRONG BEHAVIOUR. DO NOT "FIX" THIS TEST WHEN IT GOES RED.
    // ============================================================================================
    //
    // Issue #247 (P0 - characterization), docs/05-case-grain-redesign.md §C.7a.
    //
    // These tests assert that R<n> produces n+1 occurrences. That is WRONG, and it is asserted
    // here on purpose. Same spirit as Conformance/KnownGapScenarios.cs: a spec row the engine is
    // known not to honour is never omitted, it is made executable and loud.
    //
    // WHAT IS WRONG. Iso8601.Repetitions - the integer after the R - is passed straight into
    // Quartz's WithRepeatCount(n) at TimerEventSchedulerGrain.cs:87. Quartz reads that as "repeat
    // n more times AFTER the first fire", so R2/PT1S fires THREE times. ISO 8601's Rn/<interval>
    // denotes n repetitions of the interval, and every mainstream engine reads R3/PT10S as three
    // fires. §C.7a's design is explicit: R<n> = exactly n occurrences; R/ with no number =
    // unbounded; R0/ = never fires.
    //
    // WHAT HAPPENS WHEN THE FIX LANDS. These tests GO RED - that is their entire purpose. §C.7a:
    // "This must be pinned by a test before the Quartz code is deleted, so the change is visible
    // rather than silent." The correct response to a red run here is to REWRITE the expectations
    // to n (and to delete this banner), not to adjust the engine back.
    //
    // WHY NOT JUST WRITE THE CORRECT ASSERTION AND SKIP IT. Because a skipped test proves
    // nothing about today's engine, and P0's whole job is to photograph today's engine. The
    // existing TimerEventSchedulerGrainTests.TimerShouldFireOnSchedule does already depend on
    // this off-by-one - it schedules R{expectedTicks - 1} to get expectedTicks - but it does so
    // SILENTLY, as an unexplained arithmetic adjustment in a test about something else. A reader
    // would not learn from it that the engine is wrong. That is exactly the "silent rather than
    // visible" failure mode §C.7a is warning about.
    //
    // TIMING. Follows the existing timer suite exactly (TimerEventSchedulerGrainTests): a 1s
    // period, a subscription on the same StreamProviderExtensions.GetCaseEventStream identity
    // production uses, poll up to the expected count, then a short settle delay so an
    // OVER-firing schedule still has a chance to be observed before the assertion runs. This
    // path is Quartz today, not an Orleans reminder, so MinimumReminderPeriod (§D.1, and its
    // dead validator) does not apply to it.
    [Collection(ClusterCollection.Name)]
    public class TimerRepetitionOffByOneCharacterizationTests
    {
        private readonly IClusterClient _clusterClient;

        public TimerRepetitionOffByOneCharacterizationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;
        }

        // R2/PT1S. Correct per ISO 8601 and per §C.7a's design: 2 occurrences.
        // Today: 3.
        [Theory, AutoData]
        public async Task RepetitionCount__Given_R2__Then_ThreeOccurrencesFire_KNOWN_WRONG_ShouldBeTwo(Guid caseInstanceId)
        {
            var ticks = await CountTicks(caseInstanceId, repetitions: 2, expected: 3);

            ticks.Should().Be(3,
                "PINNED KNOWN-WRONG BEHAVIOUR (§C.7a): Iso8601.Repetitions goes straight into " +
                "Quartz's WithRepeatCount at TimerEventSchedulerGrain.cs:87, which means 'repeat n " +
                "more times AFTER the first fire' - so R2 fires 3 times. §C.7a's design is R<n> = " +
                "exactly n, i.e. 2. When that fix lands this assertion MUST be rewritten to 2, not " +
                "worked around");
        }

        // R1/PT1S. The smallest case that still distinguishes the two readings: correct = 1,
        // today = 2. Pinned separately because an off-by-one is easiest to misread at n=1, and
        // because a future fix that clamped at "at least one fire" would pass the R2 case above
        // while still being wrong here.
        [Theory, AutoData]
        public async Task RepetitionCount__Given_R1__Then_TwoOccurrencesFire_KNOWN_WRONG_ShouldBeOne(Guid caseInstanceId)
        {
            var ticks = await CountTicks(caseInstanceId, repetitions: 1, expected: 2);

            ticks.Should().Be(2,
                "PINNED KNOWN-WRONG BEHAVIOUR (§C.7a): R1 means one occurrence, but Quartz's " +
                "WithRepeatCount(1) means one repeat AFTER the first fire - 2 total. When §C.7a's " +
                "fix lands this assertion MUST be rewritten to 1");
        }

        // Subscribes on the same stream identity StreamProviderExtensions.GetCaseEventStream
        // builds - the identity TimerEventListenerBehavior's real subscription and TimerTickJob's
        // real publish both go through (see TimerEventSchedulerGrainTests' own remarks on why
        // subscribing to a raw StreamId instead would make the test agree with itself rather than
        // with production).
        private async Task<int> CountTicks(Guid caseInstanceId, int repetitions, int expected)
        {
            var planItemInstanceId = ShortGuid.NewGuid();
            var ticks = new List<DateTimeOffset>();

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<TimerTickedEvent>(caseInstanceId, (string)planItemInstanceId)
                .SubscribeAsync((@event, token) =>
                {
                    ticks.Add(@event.FireTime);
                    return Task.CompletedTask;
                });

            var isoPeriod = PeriodPattern.NormalizingIso.Format(Period.FromSeconds(1).Normalize());

            await _clusterClient.GetGrain<ITimerEventSchedulerGrain>(caseInstanceId).ScheduleTimer(
                planItemInstanceId,
                new Iso8601($"R{repetitions}/{isoPeriod}"),
                DateTime.UtcNow,
                new Dictionary<string, object>
                {
                    ["CaseInstanceId"] = caseInstanceId,
                    ["ElementType"] = typeof(PlanItem).Name,
                    ["PlanItemDefinition"] = typeof(TimerEventListener).Name,
                    ["ElementScope"] = "CPM.ParentStage",
                    // A plain string, matching what production passes via
                    // Host.Context["ElementInstanceId"] - TimerTickJob.Execute casts it directly.
                    ["ElementInstanceId"] = (string)planItemInstanceId
                });

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline && ticks.Count < expected)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            // The schedule is bounded, so once it reaches its ceiling it must never grow further.
            // Asserting on the instant the count first reaches `expected` would give an
            // OVER-firing schedule no chance to be observed - and over-firing is precisely the
            // failure mode this file exists to measure.
            await Task.Delay(TimeSpan.FromSeconds(3));

            return ticks.Count;
        }
    }
}
