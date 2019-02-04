using System;
using System.Linq;
using System.Reflection;
using AutoMapper;
using Flow.Grains.Infrastructure.AutoMapper;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Interfaces.Plan.Case;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Services.PlanItemBehaviorConfigurator;
using Flow.Grains.Services.PlanItemStateMachineConfigurator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Extensions;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Exceptions;

namespace Flow.Grains.Tests.SiloFixture
{
    public class ClusterFixture : IDisposable
    {
        public ISiloHost SiloHost { get; }
        public IClusterClient ClusterClient { get; }

        public ClusterFixture()
        {
            SiloHost = new SiloHostBuilder()
                .UseLocalhostClustering()

                .ConfigureServices(ConfigureServices)
                .ConfigureLogging(ConfigureLogging)

                .AddMemoryGrainStorageAsDefault() // grain state
                .AddLogStorageBasedLogConsistencyProvider()
                .AddMemoryGrainStorage("PubSubStore") // stream storage
                .AddSimpleMessageStreamProvider("Default", options => options.FireAndForgetDelivery = true) // cluster stream provider
                .UseInMemoryReminderService()

                .ConfigureApplicationParts(parts => parts
                    .AddApplicationPart(typeof(IPlanItemGrain).Assembly)
                    .AddApplicationPart(typeof(ICaseGrain).Assembly))

                .UseSiloUnobservedExceptionsHandler()
                
                .Build();

            ClusterClient = new ClientBuilder()
                .UseLocalhostClustering()

                .AddSimpleMessageStreamProvider("Default")

                .ConfigureApplicationParts(parts => parts
                    .AddApplicationPart(typeof(IPlanItemGrain).Assembly)
                    .AddApplicationPart(typeof(ICaseGrain).Assembly))

                .Build();

            SiloHost.StartAsync().GetAwaiter().GetResult();
            ClusterClient.Connect().GetAwaiter().GetResult();

            RequestContext.PropagateActivityId = true;
        }

        private static void ConfigureServices(HostBuilderContext ctx, IServiceCollection services)
        {
            services
                .AddSingleton<IClock>(SystemClock.Instance.InUtc())
                .AddRuleExecutor()
                .AddSingleton<IPlanItemBehaviorConfigurator, PlanItemBehaviorConfiguratorService>()
                .AddSingleton<IPlanItemStateMachineConfigurator, PlanItemStateMachineConfiguratorService>()
                .AddAutoMapper(cfg => cfg.AddProfile<CaseFlowProfile>(), Enumerable.Empty<Assembly>());
        }

        private void ConfigureLogging(HostBuilderContext ctx, ILoggingBuilder logging)
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
            Log.CloseAndFlush();
            SiloHost.StopAsync().GetAwaiter().GetResult();
        }
    }
}
