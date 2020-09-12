using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Flow.Grains.Events;
using Flow.Grains.Executables;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Scheduler;
using Flow.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using NodaTime;
using NodaTime.Text;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace Flow.Grains.Tests.Integration.Scheduler
{

    [Collection(ClusterCollection.Name)]
    public class TimerEventSchedulerGrainTests
    {
        private ISiloHost SiloHost { get; }
        private IClusterClient ClusterClient { get; }

        public TimerEventSchedulerGrainTests(ClusterFixture fixture)
        {
            SiloHost = fixture.SiloHost;
            ClusterClient = fixture.ClusterClient;
        }

        [Theory, AutoData]
        public async Task TimerShouldFireOnSchedule(Guid caseInstanceId)
        {
            var planItemInstanceId = ShortGuid.NewGuid();
            var expectedTicks = 3;
            var ticks = new List<DateTimeOffset>();

            var schedulerGrain = ClusterClient.GetGrain<ITimerEventSchedulerGrain>(caseInstanceId);

            await ClusterClient.GetStreamProvider("Default")
                .GetStream<TimerTickedEvent>(caseInstanceId, planItemInstanceId)
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
                    ["ElementInstanceId"] = planItemInstanceId
                });

            await Task.Factory.StartNew(() => Thread.Sleep(TimeSpan.FromSeconds(4)));

            ticks.Should().HaveCount(expectedTicks);
        }
    }
}
