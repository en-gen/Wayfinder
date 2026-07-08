using System;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Interfaces;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Quartz;

namespace Flow.Grains.Scheduler
{
    public class TimerTickJob : IJob
    {
        private IStreamProvider StreamProvider { get; }
        private ILogger Logger { get; }

        public TimerTickJob(IClusterClient client, ILogger<TimerTickJob> logger)
        {
            StreamProvider = client.GetStreamProvider("Default");
            Logger = logger;
        }

        public Task Execute(IJobExecutionContext context)
        {
            var caseInstanceId = (Guid)context.JobDetail.JobDataMap.Get("CaseInstanceId");
            var elementInstanceId = (ShortGuid)context.JobDetail.JobDataMap.Get("ElementInstanceId");

            using (Logger.BeginScope(context.JobDetail.JobDataMap))
            {
                Logger.LogInformation("{ElementType} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | timer tick occurred");
            }

            var streamId = StreamId.Create((string)elementInstanceId, caseInstanceId);
            return StreamProvider.GetStream<TimerTickedEvent>(streamId)
                .OnNextAsync(new TimerTickedEvent(
                    context.PreviousFireTimeUtc,
                    context.ScheduledFireTimeUtc,
                    context.FireTimeUtc,
                    context.NextFireTimeUtc));
        }
    }
}
