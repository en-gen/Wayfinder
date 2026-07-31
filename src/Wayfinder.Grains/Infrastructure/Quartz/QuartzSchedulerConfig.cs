using System;
using System.Collections.Specialized;

namespace Wayfinder.Grains.Infrastructure.Quartz
{
    public static class QuartzSchedulerConfig
    {
        public static readonly NameValueCollection Durable = new NameValueCollection
        {

        };

        // #197 - quartz.scheduler.instanceName used to be hardcoded to "UnitTest" here, shared by
        // every caller (every TestCluster silo AND the production silo, Wayfinder.Silo/Program.cs).
        // QuartzSchedulerFactory extends Quartz's StdSchedulerFactory, whose GetScheduler() resolves
        // through Quartz's own PROCESS-WIDE static SchedulerRepository, keyed on this name - a
        // second concern layered underneath (and invisible to) the per-caller DI singleton
        // ISchedulerFactory registration in ServiceCollectionExtensions.AddQuartz. Two callers that
        // pass the same instanceName end up sharing the exact same underlying Quartz IScheduler even
        // though each has its own DI container, and if the earlier one's owning process/cluster
        // disposes (Cluster.StopAllSilos in a TestCluster, or a silo shutting down), it calls
        // scheduler.Shutdown() on that shared instance - a Quartz scheduler cannot be restarted once
        // shut down, so the later caller silently resolves an already-dead scheduler and its timers
        // never tick. Every call site must now pass its OWN unique instanceName (see
        // Wayfinder.Silo/Program.cs and the TestCluster fixtures under
        // Wayfinder.Grains.Tests.Integration) - deliberately no default parameter here, so a new
        // caller cannot silently reintroduce the same collision by omission.
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
