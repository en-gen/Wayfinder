using System;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Spi;

namespace Flow.Grains.Infrastructure.Quartz
{
    public class QuartzJobFactory : IJobFactory
    {
        private readonly IServiceProvider _services;

        public QuartzJobFactory(IServiceProvider services)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
        }

        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler) =>
            (IJob)_services.GetRequiredService(bundle.JobDetail.JobType);

        public void ReturnJob(IJob job)
        {
            (job as IDisposable)?.Dispose();
        }
    }
}
