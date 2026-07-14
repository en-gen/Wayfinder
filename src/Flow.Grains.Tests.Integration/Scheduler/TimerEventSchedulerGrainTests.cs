using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Flow.Grains.Events;
using Flow.Grains.Executables;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Scheduler;
using Flow.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using NodaTime;
using NodaTime.Text;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace Flow.Grains.Tests.Integration.Scheduler
{

    [Collection(ClusterCollection.Name)]
    public class TimerEventSchedulerGrainTests
    {
        private readonly IClusterClient _clusterClient;

        public TimerEventSchedulerGrainTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;
        }

        [Theory, AutoData]
        public async Task TimerShouldFireOnSchedule(Guid caseInstanceId)
        {
            var planItemInstanceId = ShortGuid.NewGuid();
            var expectedTicks = 3;
            var ticks = new List<DateTimeOffset>();

            var schedulerGrain = _clusterClient.GetGrain<ITimerEventSchedulerGrain>(caseInstanceId);

            // Must match the stream identity StreamProviderExtensions.GetCaseEventStream builds -
            // the same helper TimerEventListenerBehavior's subscription (and, after the fix,
            // TimerTickJob's publish) goes through. Previously this subscribed on a raw
            // StreamId.Create((string)planItemInstanceId, caseInstanceId) - the same wrong
            // namespace TimerTickJob published to - so the test only ever passed because both sides
            // agreed on the same incorrect stream, not because ticks were actually reaching a real
            // subscriber the way production behaviors subscribe.
            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<TimerTickedEvent>(caseInstanceId, (string)planItemInstanceId)
                .SubscribeAsync((@event, token) =>
                {
                    ticks.Add(@event.FireTime);

                    return Task.CompletedTask;
                });
            var period = Period.FromSeconds(1).Normalize();
            var isoPeriod = PeriodPattern.NormalizingIso.Format(period);

            await schedulerGrain.ScheduleTimer(
                planItemInstanceId,
                new Iso8601($"R{expectedTicks - 1}/{isoPeriod}"),
                DateTime.UtcNow,
                new Dictionary<string, object>
                {
                    ["CaseInstanceId"] = caseInstanceId,
                    ["ElementType"] = typeof(PlanItem).Name,
                    ["PlanItemDefinition"] = typeof(TimerEventListener).Name,
                    ["ElementScope"] = "CPM.ParentStage",
                    // stored as string, matching what real production code passes via
                    // Host.Context["ElementInstanceId"] = IBehaviorHost.InstanceId (a plain string) -
                    // TimerTickJob.Execute reads this key back with a direct (string) cast, which only
                    // succeeds if the boxed runtime type is actually string.
                    ["ElementInstanceId"] = (string)planItemInstanceId
                });

            await Task.Delay(TimeSpan.FromSeconds(4));

            ticks.Should().HaveCount(expectedTicks);
        }

        // Regression test for #31 (findings B2/B3): ITimerEventSchedulerGrain is correctly
        // Orleans-keyed per case (IGrainWithGuidKey, addressed by caseInstanceId), but the Quartz
        // IScheduler each activation fetches from ISchedulerFactory.GetScheduler() underneath it
        // was a process-wide DI singleton built from a FIXED quartz.scheduler.instanceName (see
        // QuartzSchedulerConfig) - so every case's TimerEventSchedulerGrain shared the exact same
        // Quartz IScheduler object despite having distinct Orleans identities. The grain's
        // OnDeactivateAsync used to call _scheduler.Shutdown(false) on that shared instance, so
        // ANY one case's grain deactivating (idle collection, forced collection, redeploy) tore
        // down the shared scheduler out from under every OTHER case - and Quartz schedulers
        // cannot be restarted once shut down, making it a permanent, cross-case outage.
        //
        // This schedules independent, still-repeating timers for two different cases, forces
        // every grain activation in the cluster (including both scheduler grains) to deactivate -
        // simulating Orleans idle collection - then asserts case B's timer keeps firing
        // afterward, proving the shared scheduler survived case A's grain deactivating.
        [Theory, AutoData]
        public async Task SchedulerGrainDeactivation_ForOneCase_ShouldNotStopAnotherCasesTimer(
            Guid caseInstanceIdA, Guid caseInstanceIdB)
        {
            var planItemA = ShortGuid.NewGuid();
            var planItemB = ShortGuid.NewGuid();

            var ticksA = new List<DateTimeOffset>();
            var ticksB = new List<DateTimeOffset>();

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<TimerTickedEvent>(caseInstanceIdA, (string)planItemA)
                .SubscribeAsync((@event, token) =>
                {
                    ticksA.Add(@event.FireTime);
                    return Task.CompletedTask;
                });
            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<TimerTickedEvent>(caseInstanceIdB, (string)planItemB)
                .SubscribeAsync((@event, token) =>
                {
                    ticksB.Add(@event.FireTime);
                    return Task.CompletedTask;
                });

            var period = Period.FromSeconds(1).Normalize();
            var isoPeriod = PeriodPattern.NormalizingIso.Format(period);
            // 10 repetitions at a 1s cadence - long enough to still have pending fires left for
            // case B after the forced collection below.
            var schedule = new Iso8601($"R9/{isoPeriod}");

            await _clusterClient.GetGrain<ITimerEventSchedulerGrain>(caseInstanceIdA)
                .ScheduleTimer(planItemA, schedule, DateTime.UtcNow, ContextFor(caseInstanceIdA, planItemA));
            await _clusterClient.GetGrain<ITimerEventSchedulerGrain>(caseInstanceIdB)
                .ScheduleTimer(planItemB, schedule, DateTime.UtcNow, ContextFor(caseInstanceIdB, planItemB));

            // both timers are independently keyed and alive before any deactivation happens
            await WaitUntilAsync(() => ticksA.Count >= 1 && ticksB.Count >= 1, TimeSpan.FromSeconds(5));

            var ticksBBeforeCollection = ticksB.Count;

            await _clusterClient.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

            // case B's schedule still has repetitions remaining; it must keep ticking after the
            // forced collection above, proving its Quartz scheduler survived case A's scheduler
            // grain (and its own) deactivating.
            await WaitUntilAsync(() => ticksB.Count > ticksBBeforeCollection, TimeSpan.FromSeconds(8));

            ticksB.Count.Should().BeGreaterThan(ticksBBeforeCollection,
                "case B's timer must keep firing after case A's scheduler grain deactivates - a " +
                "per-case grain deactivating must never tear down the shared Quartz scheduler other " +
                "cases still depend on");
        }

        private static IDictionary<string, object> ContextFor(Guid caseInstanceId, string planItemInstanceId) =>
            new Dictionary<string, object>
            {
                ["CaseInstanceId"] = caseInstanceId,
                ["ElementType"] = typeof(PlanItem).Name,
                ["PlanItemDefinition"] = typeof(TimerEventListener).Name,
                ["ElementScope"] = "CPM.ParentStage",
                ["ElementInstanceId"] = planItemInstanceId
            };

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return;
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
    }
}
