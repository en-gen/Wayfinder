using System.Collections.Specialized;

namespace Flow.Grains.Infrastructure.Quartz
{
    public static class QuartzSchedulerConfig
    {
        public static readonly NameValueCollection Durable = new NameValueCollection
        {

        };

        public static readonly NameValueCollection Volatile = new NameValueCollection
        {
            { "quartz.scheduler.instanceName", "UnitTest" },
            { "quartz.jobStore.type", "Quartz.Simpl.RAMJobStore, Quartz" },
            { "quartz.threadPool.threadCount", "3" }
        };

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
