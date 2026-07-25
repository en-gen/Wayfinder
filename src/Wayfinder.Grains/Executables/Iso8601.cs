using System;
using System.Linq;
using NodaTime.Text;
using Orleans;

namespace Wayfinder.Grains.Executables
{
    [GenerateSerializer]
    public class Iso8601
    {
        [Id(0)]
        public string RawValue { get; }

        [Id(1)]
        public bool HasRepetitions { get; }
        [Id(2)]
        public int? Repetitions { get; }

        [Id(3)]
        public DateTime? Start { get; }
        [Id(4)]
        public DateTime? End { get; }
        [Id(5)]
        public TimeSpan? Duration { get; }

        public Iso8601(string iso8601)
        {
            if (string.IsNullOrWhiteSpace(iso8601)) throw new ArgumentNullException(nameof(iso8601));

            RawValue = iso8601;

            var isoParts = iso8601.Split('/');
            if (isoParts.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException($"provided ISO8601 string {iso8601} is invalid", nameof(iso8601));
            }
            var partsIndex = 0;

            /*
             * Rnn/<interval>
             */
            if (isoParts[partsIndex].StartsWith('R'))
            {
                HasRepetitions = true;
                var repetitionPart = isoParts[partsIndex];
                if (repetitionPart.Length > 1)
                {
                    if (int.TryParse(isoParts[partsIndex].Substring(1), out var repetitions))
                    {
                        Repetitions = repetitions;
                    }
                    else
                    {
                        throw new ArgumentException($"failed to parse ISO8601 repetitions value {isoParts[partsIndex]}");
                    }
                }

                partsIndex++;
            }

            /*
             * <start>/<end>
             * <start>/<duration>
             * <duration>/<end>
             */
            if (isoParts.Length - partsIndex == 2)
            {
                var firstPart = isoParts[partsIndex];
                var secondPart = isoParts[partsIndex + 1];

                if (firstPart.StartsWith('P') && secondPart.StartsWith('P'))
                {
                    throw new ArgumentException($"provided ISO8601 string {iso8601} is invalid", nameof(iso8601));
                }

                if (firstPart.StartsWith('P'))
                {
                    Duration = PeriodPattern.NormalizingIso.Parse(firstPart)
                        .GetValueOrThrow()
                        .Normalize()
                        .ToDuration()
                        .ToTimeSpan();
                }
                else
                {
                    Start = InstantPattern.ExtendedIso.Parse(firstPart)
                        .GetValueOrThrow()
                        .ToDateTimeUtc();
                }

                if (secondPart.StartsWith('P'))
                {
                    Duration = PeriodPattern.NormalizingIso.Parse(secondPart)
                        .GetValueOrThrow()
                        .Normalize()
                        .ToDuration()
                        .ToTimeSpan();
                }
                else
                {
                    End = InstantPattern.ExtendedIso.Parse(secondPart)
                        .GetValueOrThrow()
                        .ToDateTimeUtc();
                }
            }
            /*
             * <instant>
             * <duration>
             */
            else if (isoParts.Length - partsIndex == 1)
            {
                var part = isoParts[partsIndex];
                if (part.StartsWith('P'))
                {
                    Duration = PeriodPattern.NormalizingIso.Parse(part)
                        .GetValueOrThrow()
                        .Normalize()
                        .ToDuration()
                        .ToTimeSpan();
                }
                else
                {
                    Start = InstantPattern.ExtendedIso.Parse(part)
                        .GetValueOrThrow()
                        .ToDateTimeUtc();
                }
            }
            else throw new ArgumentException($"provided ISO8601 string {iso8601} is invalid", nameof(iso8601));
        }

        public override string ToString() => RawValue;
    }
}
