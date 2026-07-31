using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wayfinder.Grains.Executables;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Quartz;
using Quartz.Impl.Triggers;

namespace Wayfinder.Grains.Scheduler
{
    public class TimerEventSchedulerGrain : Grain<TimerEventSchedulerStore>, ITimerEventSchedulerGrain
    {
        private readonly ISchedulerFactory _schedulerFactory;
        private readonly ILogger _logger;

        private IScheduler _scheduler;

        private Guid _caseInstanceId;

        public TimerEventSchedulerGrain(ISchedulerFactory schedulerFactory, ILogger<TimerEventSchedulerGrain> logger)
        {
            _schedulerFactory = schedulerFactory ?? throw new ArgumentNullException(nameof(schedulerFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _caseInstanceId = this.GetPrimaryKey();

            await base.OnActivateAsync(cancellationToken);

            // based on default grain inactivity limit of 2 hours
            var reminderPeriod = TimeSpan.FromMinutes(118);

            await this.RegisterOrUpdateReminder(
                $"Keepalive_{_caseInstanceId}",
                reminderPeriod,
                reminderPeriod);

            _scheduler = await _schedulerFactory.GetScheduler();

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
            _logger.LogInformation("Scheduler activation refreshed");
            return Task.CompletedTask;
        }

        // #31 (findings B2/B3): this used to call _scheduler.Shutdown(false) here. _scheduler is
        // NOT owned by this grain activation - ISchedulerFactory is registered as a process-wide DI
        // singleton (QuartzSchedulerFactory : StdSchedulerFactory) built from whatever
        // quartz.scheduler.instanceName this silo's config uses (QuartzSchedulerConfig.Volatile(name)
        // - see its remarks; #197 made that name unique PER SILO, but every activation WITHIN one
        // silo still resolves the same instanceName), so _schedulerFactory.GetScheduler() in
        // OnActivateAsync above returns the SAME underlying Quartz IScheduler for every case's
        // TimerEventSchedulerGrain activation in this silo - the one piece of this subsystem that
        // really is a shared singleton, despite this grain itself being correctly Orleans-keyed per
        // case instance. Calling Shutdown() here genuinely shut that shared IScheduler down (Quartz
        // schedulers cannot be restarted once shut down) - but the outage this caused was NOT a
        // permanent, whole-silo one: StdSchedulerFactory.GetScheduler() evicts a shut-down entry
        // from Quartz's scheduler repository and lazily creates a fresh one, so any case activating
        // AFTER the shutdown (a brand-new activation, or the SAME case reactivating after its own
        // later deactivation) got a working scheduler again. What broke, and stayed broken for as
        // long as the activation lived, was every OTHER case's grain that was ALREADY active at the
        // moment of the Shutdown() call: _scheduler above is cached once in OnActivateAsync and
        // never refreshed, so that stale reference kept pointing at the dead scheduler for the rest
        // of that activation's lifetime - ScheduleTimer/CancelTimer would keep failing against it
        // until the grain itself eventually deactivated and reactivated. Still bad enough (a
        // long-lived activation could be broken indefinitely) that a per-case-keyed grain must never
        // destroy shared, unscoped infrastructure it doesn't exclusively own - so this override now
        // only logs the deactivation and leaves the shared scheduler running for the rest of the
        // silo's lifetime.
        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "TimerEventSchedulerGrain for case {CaseInstanceId} deactivating: {Reason} (shared Quartz scheduler keeps running for other cases)",
                _caseInstanceId,
                reason);
            return Task.CompletedTask;
        }
    }
}
