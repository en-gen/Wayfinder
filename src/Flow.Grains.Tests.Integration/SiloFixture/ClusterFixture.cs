using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using AutoMapper;
using Flow.Grains.Infrastructure.AutoMapper;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Infrastructure.Quartz;
using Flow.Grains.Interfaces.Plan.Case;
using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Services.PlanItemBehaviorConfigurator;
using Flow.Grains.Services.PlanItemStateMachineConfigurator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Extensions;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Exceptions;
using Xunit;

namespace Flow.Grains.Tests.Integration.SiloFixture
{
    public class ClusterFixture : IDisposable, IAsyncLifetime
    {
        public ISiloHost SiloHost { get; private set; }
        public IClusterClient ClusterClient { get; private set; }

        private bool _disposed = false;

        public async Task InitializeAsync()
        {
            SiloHost = new SiloHostBuilder()
                .UseLocalhostClustering()
                
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = "integration";
                    options.ServiceId = "Case.Flow";
                })

                .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                
                .ConfigureServices(ConfigureServices)
                .ConfigureLogging(ConfigureLogging)

                .AddMemoryGrainStorageAsDefault() // grain state
                .AddLogStorageBasedLogConsistencyProvider() // journaled grain
                .AddMemoryGrainStorage("PubSubStore") // stream storage
                .AddSimpleMessageStreamProvider("Default", options => options.FireAndForgetDelivery = true) // cluster stream provider
                .UseInMemoryReminderService()

                .ConfigureApplicationParts(parts => parts
                    .AddApplicationPart(typeof(IPlanItemInternalGrain).Assembly)
                    .WithReferences())

                .UseSiloUnobservedExceptionsHandler()
                
                .Build();

            await SiloHost.StartAsync();

            ClusterClient = SiloHost.Services.GetRequiredService<IClusterClient>();

            await ClusterClient.Connect();
        }

        public async Task DisposeAsync()
        {
            await ClusterClient.Close();
            await SiloHost.StopAsync();
        }

        private static void ConfigureServices(HostBuilderContext ctx, IServiceCollection services)
        {
            services
                .AddSingleton<IClock>(SystemClock.Instance.InUtc())
                .AddRuleExecutor()
                .AddSingleton<IPlanItemBehaviorConfigurator, PlanItemBehaviorConfiguratorService>()
                .AddSingleton<IPlanItemStateMachineConfigurator, PlanItemStateMachineConfiguratorService>()
                .AddAutoMapper(cfg => cfg.AddProfile<CaseFlowProfile>(), Enumerable.Empty<Assembly>())
                .AddQuartz(QuartzSchedulerConfig.Volatile);
        }

        private static void ConfigureLogging(HostBuilderContext ctx, ILoggingBuilder logging)
        {
            var levelSwitch = new LoggingLevelSwitch
            {
                MinimumLevel = LogEventLevel.Debug
            };

            logging.AddSerilog(new LoggerConfiguration()
                .MinimumLevel.ControlledBy(levelSwitch)
                .Enrich.FromLogContext()
                .Enrich.WithExceptionDetails()
                .WriteTo.Seq(
                    "http://localhost:5341",
                    controlLevelSwitch: levelSwitch
                )
                .CreateLogger());
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                Log.CloseAndFlush();
            }

            _disposed = true;
        }
    }
}
