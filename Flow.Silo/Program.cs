using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using AutoMapper;
using Flow.Grains.Infrastructure.AutoMapper;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Infrastructure.Quartz;
using Flow.Grains.Plan.Case;
using Flow.Grains.Services.PlanItemBehaviorConfigurator;
using Flow.Grains.Services.PlanItemStateMachineConfigurator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Exceptions;
using HostBuilderContext = Microsoft.Extensions.Hosting.HostBuilderContext;

namespace Flow.Silo
{
    public class Program
    {
        private static ISiloHost SiloHost { get; set; }

        public static async Task<int> Main(string[] args)
        {
            var levelSwitch = new LoggingLevelSwitch
            {
                MinimumLevel = LogEventLevel.Debug
            };
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(levelSwitch)
                .Enrich.FromLogContext()
                .Enrich.WithExceptionDetails()
                .WriteTo.Seq(
                    "http://localhost:5341",
                    apiKey: null,
                    controlLevelSwitch: levelSwitch
                )
                .CreateLogger();
            
            Log.Logger.Debug("initializing silo");

            try
            {
                var host = new HostBuilder()
                    .ConfigureServices(ConfigureServices)
                    .ConfigureLogging(builder => builder.AddSerilog(Log.Logger))
                    .UseOrleans((context, siloBuilder) =>
                    {
                        siloBuilder
                            .UseAzureStorageClustering(options => options.ConnectionString = "UseDevelopmentStorage=true")

                            .Configure<ClusterOptions>(options =>
                            {
                                options.ClusterId = "development";
                                options.ServiceId = "Case.Flow";
                            })
                            .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = Dns.GetHostAddresses(Dns.GetHostName())
                                .Single(x => x.AddressFamily == AddressFamily.InterNetwork && x.ToString().StartsWith("192.168."))) // TODO: even if this is a config point, I don't like it

                            .AddAzureTableGrainStorageAsDefault(options => options.ConnectionString = "UseDevelopmentStorage=true") // grain state
                            .AddLogStorageBasedLogConsistencyProvider() // journaled grain

                            .AddSimpleMessageStreamProvider("Default", options => options.FireAndForgetDelivery = true) // cluster stream provider
                            .AddAzureTableGrainStorage("PubSubStore", options => options.ConnectionString = "UseDevelopmentStorage=true") // stream state
                            .UseAzureTableReminderService("UseDevelopmentStorage=true")
                            
                            .ConfigureApplicationParts(parts => parts
                                .AddApplicationPart(typeof(CaseGrain).Assembly) // Flow.Grains
                                .WithReferences()); //Flow.Grains.Interfaces
                    })
                    .Build();

                await host.RunAsync();
                return 0;
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "failure during cluster startup");
                Log.CloseAndFlush();
                return 1;
            }
        }

        private static void ConfigureServices(HostBuilderContext ctx, IServiceCollection services)
        {
            services
                .AddRuleExecutor()
                .AddSingleton<IPlanItemBehaviorConfigurator, PlanItemBehaviorConfiguratorService>()
                .AddSingleton<IPlanItemStateMachineConfigurator, PlanItemStateMachineConfiguratorService>()
                .AddAutoMapper(cfg => cfg.AddProfile<CaseFlowProfile>(), Enumerable.Empty<Assembly>())
                .AddQuartz(QuartzSchedulerConfig.Volatile);
        }
    }
}
