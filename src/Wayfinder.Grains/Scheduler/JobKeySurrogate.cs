using Orleans;
using Quartz;

namespace Flow.Grains.Scheduler
{
    // Quartz.JobKey is a foreign (out-of-our-control) sealed type with no Orleans serializer, but
    // TimerEventSchedulerStore.JobKeys persists it as grain state. Surrogates are the documented
    // Orleans approach for serializing foreign types: see
    // https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/serialization#surrogates-for-serializing-foreign-types
    [GenerateSerializer]
    public struct JobKeySurrogate
    {
        [Id(0)]
        public string Name;

        [Id(1)]
        public string Group;
    }

    [RegisterConverter]
    public sealed class JobKeySurrogateConverter : IConverter<JobKey, JobKeySurrogate>
    {
        public JobKey ConvertFromSurrogate(in JobKeySurrogate surrogate) =>
            new JobKey(surrogate.Name, surrogate.Group);

        public JobKeySurrogate ConvertToSurrogate(in JobKey value) =>
            new JobKeySurrogate
            {
                Name = value.Name,
                Group = value.Group
            };
    }
}
