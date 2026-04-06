using System;
using System.Collections.Generic;
using System.Text;
using AutoFixture.Xunit2;
using Flow.Grains.Executables;
using Flow.Grains.Plan.PlanItem.Behaviors.Stores;
using Flow.Grains.Plan.PlanItem.Events;
using FluentAssertions;
using NodaTime;
using Xunit;

namespace Flow.Grains.Tests.Plan.PlanItem.Behaviors.Stores
{
    public class TimerEventListenerBehaviorStoreTests
    {
        [Fact]
        public void Apply__When_TimerStartTriggerOccurred__Then_TimerStartWhenEventOccurred()
        {
            var @event = new TimerStartTriggerOccurred();

            var subject = new TimerEventListenerBehaviorStore();

            subject.Apply(@event);

            subject.TimerStart.Should()
                .HaveValue()
                .And.Be(@event.Occurred);
        }

        [Theory, AutoData]
        public void Apply__When_TimerExpressionEvaluated__Then_TimerScheduleAndErrorSet(string error)
        {
            var @event = new TimerExpressionEvaluated
            {
                Result = new Iso8601(SystemClock.Instance.GetCurrentInstant().ToString()),
                Error = error
            };

            var subject = new TimerEventListenerBehaviorStore();
            
            subject.Apply(@event);

            subject.TimerSchedule.Should().Be(@event.Result);
            subject.TimerScheduleEvaluationError.Should().Be(@event.Error);
        }
    }
}
