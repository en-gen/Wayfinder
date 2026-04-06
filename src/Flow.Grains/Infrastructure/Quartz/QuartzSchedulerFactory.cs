using System.Collections.Specialized;
using Quartz;
using Quartz.Core;
using Quartz.Impl;
using Quartz.Spi;

namespace Flow.Grains.Infrastructure.Quartz
{
    public class QuartzSchedulerFactory : StdSchedulerFactory
    {
        private IJobFactory JobFactory { get; }

        public QuartzSchedulerFactory(IJobFactory jobFactory, NameValueCollection config) :
            base(config)
        {
            JobFactory = jobFactory;
        }
        
        protected override IScheduler Instantiate(QuartzSchedulerResources qsr, QuartzScheduler qs)
        {
            qs.JobFactory = JobFactory;
            return base.Instantiate(qsr, qs);
        }
    }
}
