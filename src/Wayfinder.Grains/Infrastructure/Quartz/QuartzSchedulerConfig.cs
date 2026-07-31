using System;
using System.Collections.Specialized;

namespace Wayfinder.Grains.Infrastructure.Quartz
{
    public static class QuartzSchedulerConfig
    {
        // #197 - quartz.scheduler.instanceName used to be hardcoded to "UnitTest" here, shared by
        // every caller (every TestCluster silo AND the production silo, Wayfinder.Silo/Program.cs).
        // QuartzSchedulerFactory extends Quartz's StdSchedulerFactory, whose GetScheduler() resolves
        // through Quartz's own PROCESS-WIDE static SchedulerRepository, keyed on this name - a
        // second concern layered underneath (and invisible to) the per-caller DI singleton
        // ISchedulerFactory registration in ServiceCollectionExtensions.AddQuartz.
        //
        // The actual failure mode (verified against Quartz 3.18.2 by instrumenting the repository
        // directly - NOT a disposal/shutdown race, see below): GetScheduler() only calls
        // QuartzSchedulerFactory.Instantiate (which sets qs.JobFactory = the caller's own
        // IJobFactory) the FIRST time a scheduler is created under a given instanceName; if the
        // repository already holds a live scheduler under that name, GetScheduler() just returns
        // that existing object as-is. So two independently-lifecycled callers (each with its own DI
        // container/IServiceProvider/IClusterClient) passing the SAME instanceName resolve the exact
        // same IScheduler instance, but it stays permanently wired to the FIRST caller's JobFactory
        // (and therefore the first caller's IClusterClient/IServiceProvider) - regardless of whether
        // that first caller is still alive. Jobs the SECOND caller schedules still execute through
        // the FIRST caller's JobFactory: ticks get published into the wrong owner's grain/stream
        // space (or, once the first caller has since disposed, execute against its already-torn-down
        // DI container). Nothing here ever calls scheduler.Shutdown() - #31 removed the only call
        // site (see TimerEventSchedulerGrain.OnDeactivateAsync's remarks), QuartzSchedulerFactory
        // isn't IDisposable, and no IScheduler is registered in DI - so a disposed cluster's
        // scheduler is orphaned ALIVE, not shut down; even if something did shut it down,
        // StdSchedulerFactory.GetScheduler() evicts a shutdown entry from the repository and lazily
        // creates a fresh one, so a stale/dead scheduler was never the actual risk.
        //
        // Every call site must now pass its OWN unique instanceName (see Wayfinder.Silo/Program.cs
        // and the TestCluster fixtures under Wayfinder.Grains.Tests.Integration) - deliberately no
        // default parameter here, so a new caller cannot silently reintroduce the same collision by
        // omission.
        public static NameValueCollection Volatile(string instanceName)
        {
            if (string.IsNullOrWhiteSpace(instanceName))
            {
                throw new ArgumentException(
                    "Quartz instanceName must be a non-empty, unique-per-scheduler value - see the " +
                    "#197 remarks above.", nameof(instanceName));
            }

            return new NameValueCollection
            {
                { "quartz.scheduler.instanceName", instanceName },
                { "quartz.jobStore.type", "Quartz.Simpl.RAMJobStore, Quartz" },
                { "quartz.threadPool.threadCount", "3" }
            };
        }

        //_quartzConfig["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.JobStoreTX, Quartz";
        //_quartzConfig["quartz.jobStore.driverDelegateType"] = "Quartz.Impl.AdoJobStore.SqlServerDelegate, Quartz";
        //_quartzConfig["quartz.jobStore.tablePrefix"] = "quartz.";
        //_quartzConfig["quartz.jobStore.useProperties"] = "true";
        //_quartzConfig["quartz.jobStore.dataSource"] = "default";
        //_quartzConfig["quartz.jobStore.clustered"] = "true";
        //_quartzConfig["quartz.dataSource.default.connectionString"] = connectionString;
        //_quartzConfig["quartz.dataSource.default.provider"] = "SqlServer-20";
        //_quartzConfig["quartz.scheduler.instanceId"] = "AUTO";
    }
}
