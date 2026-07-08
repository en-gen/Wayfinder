using System;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.Case;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Tests.Integration.SiloFixture;
using Flow.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using NodaTime;
using Orleans;
using Xunit;

namespace Flow.Grains.Tests.Integration.Scheduler
{
    // End-to-end proof for fix #5 (work item #13): Quartz tick -> TimerTickJob publish ->
    // TimerEventListenerBehavior subscription -> Occur transition -> Completed.
    //
    // Before the fix, TimerTickJob built its stream identity as
    // StreamId.Create((string)elementInstanceId, caseInstanceId) - a raw instance-id namespace with
    // no "TimerTickedEvent:" prefix - while the behavior always subscribed via
    // StreamProviderExtensions.GetCaseEventStream<TimerTickedEvent>(caseInstanceId, instanceId),
    // whose namespace is "TimerTickedEvent:{instanceId}". The two streams never matched, so a real
    // Quartz-scheduled tick was published but never delivered to any subscriber, and a
    // TimerEventListener plan item would sit in Available forever. This test defines a case with a
    // TimerEventListener plan item on a short timer expression, starts it in a real TestCluster, and
    // asserts the plan item actually reaches Completed within a timeout - something the previous
    // TimerEventSchedulerGrainTests test could not catch, because it constructed its own stream
    // subscription with the same wrong shape as the (formerly buggy) publisher instead of going
    // through the same helper a real behavior uses.
    [Collection(ClusterCollection.Name)]
    public class TimerEventListenerEndToEndTests
    {
        private IClusterClient ClusterClient { get; }

        public TimerEventListenerEndToEndTests(ClusterFixture fixture)
        {
            ClusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        [Theory, AutoData]
        public async Task TimerEventListener__Given_ShortTimerExpression__When_Started__Then_ReachesCompletedViaQuartzTick(Guid caseInstanceId)
        {
            var timerEventListener = new TimerEventListener
            {
                Id = "TimerListenerA",
                // no timerStart trigger, so ScheduleTimer's StartAt falls back to DateTime.UtcNow;
                // a short duration-only expression (no repetitions) fires the Quartz trigger exactly
                // once, shortly after scheduling.
                TimerExpression = Timers.TimerExpression(Period.FromSeconds(1).Normalize())
            };

            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemA",
                DefinitionRef = timerEventListener.Id
            };

            var @case = new Case
            {
                CasePlanModel = new Stage
                {
                    Id = "CPM",
                    PlanItemDefinitions = { timerEventListener },
                    PlanItems = { planItem }
                }
            };

            await ClusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, @case.Id)
                .Define(@case);

            var subject = ClusterClient
                .GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{@case.CasePlanModel.Id}.{planItem.Id}");

            await subject.Define(@case.Id, planItem);

            var afterCreate = await subject.Trigger(PlanItemTransition.Create);
            afterCreate.PlanItemState.Should().Be(PlanItemState.Available, "the plan item must reach Available before its timer can be scheduled");

            var reachedCompleted = false;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var lastObservedState = afterCreate.PlanItemState;

            while (DateTime.UtcNow < deadline)
            {
                var snapshot = await subject.GetSnapshot();
                lastObservedState = snapshot.PlanItemState;

                if (snapshot.PlanItemState == PlanItemState.Completed)
                {
                    reachedCompleted = true;
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }

            reachedCompleted.Should().BeTrue($"the Quartz tick should reach TimerEventListenerBehavior via the corrected stream and fire the Occur transition to Completed (last observed state: {lastObservedState})");
        }
    }
}
