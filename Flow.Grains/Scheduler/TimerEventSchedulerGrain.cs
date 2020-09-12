using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flow.Grains.Executables;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Quartz;
using Quartz.Impl.Triggers;

namespace Flow.Grains.Scheduler
{
    public class TimerEventSchedulerGrain : Grain<TimerEventSchedulerStore>, ITimerEventSchedulerGrain
    {
        private ISchedulerFactory SchedulerFactory { get; }
        private ILogger Logger { get; }
        
        private IScheduler _scheduler;

        private Guid _caseInstanceId;

        public TimerEventSchedulerGrain(ISchedulerFactory schedulerFactory, ILogger<TimerEventSchedulerGrain> logger)
        {
            SchedulerFactory = schedulerFactory;
            Logger = logger;
        }

        public override async Task OnActivateAsync()
        {
            _caseInstanceId = this.GetPrimaryKey();

            await base.OnActivateAsync();

            // based on default grain inactivity limit of 2 hours
            var reminderPeriod = TimeSpan.FromMinutes(118);

            await RegisterOrUpdateReminder(
                $"Keepalive_{_caseInstanceId}",
                reminderPeriod,
                reminderPeriod);

            _scheduler = await SchedulerFactory.GetScheduler();
            
            await _scheduler.Start();
        }

        public Task ScheduleTimer(string planItemInstanceId, Iso8601 schedule, DateTime? timerStart, IDictionary<string, object> context)
        {
            var job = ConfigureJob(planItemInstanceId, context);
            State.JobKeys[planItemInstanceId] = job.Key;
            return Task.WhenAll(
                WriteStateAsync(),
                _scheduler.ScheduleJob(job, ConfigureTrigger(schedule, timerStart)));
        }

        public async Task CancelTimer(string planItemInstanceId)
        {
            if (State.JobKeys.TryGetValue(planItemInstanceId, out var key))
            {
                await _scheduler.DeleteJob(key);
            }
        }

        private IJobDetail ConfigureJob(string planItemInstanceId, IDictionary<string, object> context) =>
            JobBuilder.Create<TimerTickJob>()
                .WithIdentity($"{_caseInstanceId}_{planItemInstanceId}")
                .UsingJobData(new JobDataMap(context))
                .Build();

        private static ITrigger ConfigureTrigger(Iso8601 schedule, DateTime? timerStart)
        {
            var triggerBuilder = TriggerBuilder.Create();

            var startAt = timerStart ?? schedule.Start;

            triggerBuilder
                .StartAt(startAt ?? DateTime.UtcNow)
                .WithSimpleSchedule(scheduleBuilder =>
                {
                    scheduleBuilder
                        .WithInterval(schedule.Duration ?? TimeSpan.Zero);

                    if (schedule.HasRepetitions)
                    {
                        scheduleBuilder
                            .WithRepeatCount(schedule.Repetitions ?? SimpleTriggerImpl.RepeatIndefinitely);
                    }
                })
                .EndAt(schedule.End);


            return triggerBuilder.Build();
        }

        // keeps grain active, prevents deactivation
        public Task ReceiveReminder(string reminderName, TickStatus status)
        {
            Logger.LogInformation("Scheduler activation refreshed");
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync()
        {
            Logger.LogWarning("Scheduler shutting down");
            return _scheduler.Shutdown(false);
        }
    }
}
