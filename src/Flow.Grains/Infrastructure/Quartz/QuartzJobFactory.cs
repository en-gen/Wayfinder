using System;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Spi;

namespace Flow.Grains.Infrastructure.Quartz
{
    public class QuartzJobFactory : IJobFactory
    {
        private IServiceProvider Services { get; }

        public QuartzJobFactory(IServiceProvider services)
        {
            Services = services;
        }

        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler) =>
            (IJob)Services.GetRequiredService(bundle.JobDetail.JobType);

        public void ReturnJob(IJob job)
        {
            (job as IDisposable)?.Dispose();
        }
    }
}
