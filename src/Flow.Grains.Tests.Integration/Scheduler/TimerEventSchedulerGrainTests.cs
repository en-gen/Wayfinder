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
    }
}
