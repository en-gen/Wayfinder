using System;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Interfaces;
using Microsoft.Extensions.Logging;
using Orleans;
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
            // Read directly as string rather than casting through (ShortGuid): Host.Context (the
            // source of this JobDataMap - see TimerEventListenerBehavior/CmmnElementGrain) stores
            // IBehaviorHost.InstanceId, which is a plain string. A direct (ShortGuid) cast on a
            // boxed object whose runtime type is string throws InvalidCastException - ShortGuid's
            // implicit string conversion operator only applies when the compiler knows the
            // compile-time source type is string, not when unboxing from object - so this job would
            // fault before ever reaching the stream publish below, independent of the stream
            // identity mismatch fixed here.
            var elementInstanceId = (string)context.JobDetail.JobDataMap.Get("ElementInstanceId");

            using (Logger.BeginScope(context.JobDetail.JobDataMap))
            {
                Logger.LogInformation("{ElementType} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | timer tick occurred");
            }

            // Must build the stream identity exactly like StreamProviderExtensions.GetCaseEventStream
            // does (namespace "{EventTypeName}:{eventSource}", key = caseInstanceId), which is the
            // same helper TimerEventListenerBehavior's subscription goes through via
            // CmmnElementGrain.SubscribeTo<TimerTickedEvent>(Host.InstanceId, ...). Previously this
            // built StreamId.Create((string)elementInstanceId, caseInstanceId) directly - a raw
            // instance id namespace with no "TimerTickedEvent:" prefix - so ticks were published to a
            // stream nobody ever subscribed to.
            return StreamProvider.GetCaseEventStream<TimerTickedEvent>(caseInstanceId, elementInstanceId)
                .OnNextAsync(new TimerTickedEvent(
                    context.PreviousFireTimeUtc,
                    context.ScheduledFireTimeUtc,
                    context.FireTimeUtc,
                    context.NextFireTimeUtc));
        }
    }
}
