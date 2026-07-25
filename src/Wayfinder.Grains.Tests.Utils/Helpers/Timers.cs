using System;
using System.Text;
using Wayfinder.Grains.Interfaces.Model;
using NodaTime;
using NodaTime.Text;

namespace Wayfinder.Grains.Tests.Utils.Helpers
{
    public static class Timers
    {
        public static Expression TimerExpression(Instant start, Instant? end = null, int? repetitions = null)
        {
            var builder = new StringBuilder("'");

            if (repetitions.HasValue)
            {
                builder.Append($"R{repetitions.Value}/");
            }

            builder.Append(InstantPattern.ExtendedIso.Format(start));

            if (end.HasValue)
            {
                builder.Append($"/{InstantPattern.ExtendedIso.Format(end.Value)}");
            }

            builder.Append("'");

            return new Expression
            {
                Id = Guid.NewGuid().ToString(),
                Language = ExpressionLanguage.Jint,
                Body = builder.ToString()
            };
        }

        public static Expression TimerExpression(Period period, Instant? start = null, int? repetitions = null)
        {
            var builder = new StringBuilder("'");

            if (repetitions != null)
            {
                builder.Append($"R{repetitions}/");
            }

            if (start.HasValue)
            {
                builder.Append($"{InstantPattern.ExtendedIso.Format(start.Value)}/");
            }

            builder.Append($"{PeriodPattern.NormalizingIso.Format(period)}'");

            return new Expression
            {
                Id = Guid.NewGuid().ToString(),
                Language = ExpressionLanguage.Jint,
                Body = builder.ToString()
            };
        }
    }
}
