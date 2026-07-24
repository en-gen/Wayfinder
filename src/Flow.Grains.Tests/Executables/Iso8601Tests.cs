using System;
using AutoFixture.Xunit2;
using Flow.Grains.Executables;
using NodaTime;
using NodaTime.Text;
using Xunit;

namespace Flow.Grains.Tests.Executables
{
    public class Iso8601Tests
    {
        [Fact] // <datetime>
        public void Ctor__Given_DateTime__Then_StartSet()
        {
            var now = DateTime.UtcNow;
            var iso = new Iso8601(now.ToString("O"));

            Assert.Null(iso.Repetitions);
            Assert.False(iso.HasRepetitions);
            Assert.NotNull(iso.Start);
            Assert.Equal(now, iso.Start.Value);
            Assert.Null(iso.End);
            Assert.Null(iso.Duration);
        }

        [Theory, AutoData] // <duration>
        public void Ctor__Given_Period__Then_PeriodSet
            (int days, int hours, int minutes, int seconds)
        {
            var period = new PeriodBuilder
            {
                Days = days,
                Hours = hours,
                Minutes = minutes,
                Seconds = seconds
            }.Build().Normalize();
            var isoString = PeriodPattern.NormalizingIso.Format(period);
            var iso = new Iso8601(isoString);

            Assert.Null(iso.Repetitions);
            Assert.False(iso.HasRepetitions);
            Assert.Null(iso.Start);
            Assert.Null(iso.End);
            Assert.NotNull(iso.Duration);
            Assert.Equal(period.ToDuration().ToTimeSpan(), iso.Duration);
        }

        [Theory, AutoData] // R#/<duration>
        public void Ctor__Given_RepeatedPeriod__When_BoundedRepetitions__Then_RepetitionAndPeriodSet
            (int repetitions, int days, int hours, int minutes, int seconds)
        {
            var period = new PeriodBuilder
            {
                Days = days,
                Hours = hours,
                Minutes = minutes,
                Seconds = seconds
            }.Build().Normalize();

            var isoString = $"R{repetitions}/{PeriodPattern.NormalizingIso.Format(period)}";
            var iso = new Iso8601(isoString);

            Assert.NotNull(iso.Repetitions);
            Assert.Equal(repetitions, iso.Repetitions);
            Assert.True(iso.HasRepetitions);
            Assert.Null(iso.Start);
            Assert.Null(iso.End);
            Assert.NotNull(iso.Duration);
            Assert.Equal(period.ToDuration().ToTimeSpan(), iso.Duration);
        }

        [Theory, AutoData] // R/<duration>
        public void Ctor__Given_RepeatedPeriod__When_UnboundedRepetitions__Then_RepetitionAndPeriodSet
            (int days, int hours, int minutes, int seconds)
        {
            var period = new PeriodBuilder
            {
                Days = days,
                Hours = hours,
                Minutes = minutes,
                Seconds = seconds
            }.Build().Normalize();

            var isoString = $"R/{PeriodPattern.NormalizingIso.Format(period)}";
            var iso = new Iso8601(isoString);

            Assert.Null(iso.Repetitions);
            Assert.True(iso.HasRepetitions);
            Assert.Null(iso.Start);
            Assert.Null(iso.End);
            Assert.NotNull(iso.Duration);
            Assert.Equal(period.ToDuration().ToTimeSpan(), iso.Duration);
        }

        [Theory, AutoData] // <start>/<duration>
        public void Ctor__Given_PeriodWithStart__Then_PeriodWithStart
            (int days, int hours, int minutes, int seconds)
        {
            var start = DateTime.UtcNow;
            var period = new PeriodBuilder
            {
                Days = days,
                Hours = hours,
                Minutes = minutes,
                Seconds = seconds
            }.Build().Normalize();

            var isoString = $"{start:O}/{PeriodPattern.NormalizingIso.Format(period)}";
            var iso = new Iso8601(isoString);

            Assert.Null(iso.Repetitions);
            Assert.False(iso.HasRepetitions);
            Assert.NotNull(iso.Start);
            Assert.Equal(start, iso.Start.Value);
            Assert.Null(iso.End);
            Assert.NotNull(iso.Duration);
            Assert.Equal(period.ToDuration().ToTimeSpan(), iso.Duration);
        }

        [Theory, AutoData] // R#/<start>/<duration>
        public void Ctor__Given_PeriodWithStartWithRepetition__When_BoundedRepetition__Then_PeriodWithStart
            (int repetitions, int days, int hours, int minutes, int seconds)
        {
            var start = DateTime.UtcNow;
            var period = new PeriodBuilder
            {
                Days = days,
                Hours = hours,
                Minutes = minutes,
                Seconds = seconds
            }.Build().Normalize();

            var isoString = $"R{repetitions}/{start:O}/{PeriodPattern.NormalizingIso.Format(period)}";
            var iso = new Iso8601(isoString);

            Assert.NotNull(iso.Repetitions);
            Assert.Equal(repetitions, iso.Repetitions);
            Assert.True(iso.HasRepetitions);
            Assert.NotNull(iso.Start);
            Assert.Equal(start, iso.Start.Value);
            Assert.Null(iso.End);
            Assert.NotNull(iso.Duration);
            Assert.Equal(period.ToDuration().ToTimeSpan(), iso.Duration);
        }

        [Theory, AutoData] // R/<start>/<duration>
        public void Ctor__Given_PeriodWithStartWithRepetition__When_UnboundedRepetition__Then_PeriodWithStart
            (int days, int hours, int minutes, int seconds)
        {
            var start = DateTime.UtcNow;
            var period = new PeriodBuilder
            {
                Days = days,
                Hours = hours,
                Minutes = minutes,
                Seconds = seconds
            }.Build().Normalize();

            var isoString = $"R/{start:O}/{PeriodPattern.NormalizingIso.Format(period)}";
            var iso = new Iso8601(isoString);

            Assert.Null(iso.Repetitions);
            Assert.True(iso.HasRepetitions);
            Assert.NotNull(iso.Start);
            Assert.Equal(start, iso.Start.Value);
            Assert.Null(iso.End);
            Assert.NotNull(iso.Duration);
            Assert.Equal(period.ToDuration().ToTimeSpan(), iso.Duration);
        }

        [Theory, AutoData] // <duration>/<end>
        public void Ctor__Given_PeriodWithEnd__Then_PeriodWithEnd
            (int days, int hours, int minutes, int seconds)
        {
            var end = DateTime.UtcNow.AddDays(7);
            var period = new PeriodBuilder
            {
                Days = days,
                Hours = hours,
                Minutes = minutes,
                Seconds = seconds
            }.Build().Normalize();

            var isoString = $"{PeriodPattern.NormalizingIso.Format(period)}/{end:O}";
            var iso = new Iso8601(isoString);

            Assert.Null(iso.Repetitions);
            Assert.False(iso.HasRepetitions);
            Assert.Null(iso.Start);
            Assert.NotNull(iso.End);
            Assert.Equal(end, iso.End.Value);
            Assert.NotNull(iso.Duration);
            Assert.Equal(period.ToDuration().ToTimeSpan(), iso.Duration);
        }

        [Theory, AutoData] // R#/<duration>/<end>
        public void Ctor__Given_PeriodWithEndWithRepetition__When_BoundedRepetition__Then_PeriodWithEnd
            (int repetitions, int days, int hours, int minutes, int seconds)
        {
            var end = DateTime.UtcNow.AddDays(7);
            var period = new PeriodBuilder
            {
                Days = days,
                Hours = hours,
                Minutes = minutes,
                Seconds = seconds
            }.Build().Normalize();

            var isoString = $"R{repetitions}/{PeriodPattern.NormalizingIso.Format(period)}/{end:O}";
            var iso = new Iso8601(isoString);

            Assert.NotNull(iso.Repetitions);
            Assert.Equal(repetitions, iso.Repetitions);
            Assert.True(iso.HasRepetitions);
            Assert.Null(iso.Start);
            Assert.NotNull(iso.End);
            Assert.Equal(end, iso.End.Value);
            Assert.NotNull(iso.Duration);
            Assert.Equal(period.ToDuration().ToTimeSpan(), iso.Duration);
        }

        [Theory, AutoData] // R/<duration>/<end>
        public void Ctor__Given_PeriodWithEndWithRepetition__When_UnboundedRepetition__Then_PeriodWithEnd
            (int days, int hours, int minutes, int seconds)
        {
            var end = DateTime.UtcNow.AddDays(7);
            var period = new PeriodBuilder
            {
                Days = days,
                Hours = hours,
                Minutes = minutes,
                Seconds = seconds
            }.Build().Normalize();

            var isoString = $"R/{PeriodPattern.NormalizingIso.Format(period)}/{end:O}";
            var iso = new Iso8601(isoString);

            Assert.Null(iso.Repetitions);
            Assert.True(iso.HasRepetitions);
            Assert.Null(iso.Start);
            Assert.NotNull(iso.End);
            Assert.Equal(end, iso.End.Value);
            Assert.NotNull(iso.Duration);
            Assert.Equal(period.ToDuration().ToTimeSpan(), iso.Duration);
        }

        [Fact] // <start>/<end>
        public void Ctor__Given_StartAndEndDate__Then_StartAndEnd()
        {
            var start = DateTime.UtcNow;
            var end = DateTime.UtcNow.AddDays(1);

            var isoString = $"{start:O}/{end:O}";
            var iso = new Iso8601(isoString);

            Assert.Null(iso.Repetitions);
            Assert.False(iso.HasRepetitions);
            Assert.NotNull(iso.Start);
            Assert.Equal(start, iso.Start.Value);
            Assert.NotNull(iso.End);
            Assert.Equal(end, iso.End.Value);
            Assert.Null(iso.Duration);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("\t")]
        [InlineData("\n")]
        public void Ctor__When_NullOrEmptyArg__Then_ArgNullEx(string iso8601Str)
        {
            Assert.Throws<ArgumentNullException>(() => new Iso8601(iso8601Str));
        }

        [Fact]
        public void Ctor__When_InvalidRepetition__Then_ArgEx()
        {
            Assert.Throws<ArgumentException>(() => new Iso8601($"RX/{DateTime.UtcNow:O}"));
        }

        [Theory, AutoData]
        public void Ctor__When_IntervalOfTwoPeriods__Then_ArgEx
            (int days, int hours, int minutes, int seconds)
        {
            var period = PeriodPattern.NormalizingIso
                .Format(new PeriodBuilder
                {
                    Days = days,
                    Hours = hours,
                    Minutes = minutes,
                    Seconds = seconds
                }.Build().Normalize());

            Assert.Throws<ArgumentException>(() => new Iso8601($"{period}/{period}"));
        }

        [Fact]
        public void Ctor__When_TooManyParts__Then_ArgEx()
        {
            var now = DateTime.UtcNow;
            Assert.Throws<ArgumentException>(() => new Iso8601($"{now:O}/{now:O}/{now:O}"));
        }

        [Fact]
        public void Ctor__When_NotEnoughParts__Then_ArgEx()
        {
            Assert.Throws<ArgumentException>(() => new Iso8601("R5/"));
        }

        [Fact]
        public void ToString__Given_ValidIso__Then_ReturnOriginalRawValue()
        {
            var raw = $"R4/{DateTime.UtcNow:O}";

            var iso = new Iso8601(raw);

            Assert.Equal(raw, iso.RawValue);
            Assert.Equal(raw, iso.ToString());
        }
    }
}
