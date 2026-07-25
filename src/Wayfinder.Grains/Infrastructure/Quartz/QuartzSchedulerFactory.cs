using System;
using System.Collections.Specialized;
using Quartz;
using Quartz.Core;
using Quartz.Impl;
using Quartz.Spi;

namespace Wayfinder.Grains.Infrastructure.Quartz
{
    public class QuartzSchedulerFactory : StdSchedulerFactory
    {
        private readonly IJobFactory _jobFactory;

        public QuartzSchedulerFactory(IJobFactory jobFactory, NameValueCollection config) :
            base(config)
        {
            _jobFactory = jobFactory ?? throw new ArgumentNullException(nameof(jobFactory));
        }

        protected override IScheduler Instantiate(QuartzSchedulerResources qsr, QuartzScheduler qs)
        {
            qs.JobFactory = _jobFactory;
            return base.Instantiate(qsr, qs);
        }
    }
}
