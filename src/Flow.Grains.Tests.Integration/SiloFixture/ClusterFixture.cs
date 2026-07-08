using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using AutoMapper;
using Flow.Grains.Infrastructure.AutoMapper;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Infrastructure.Quartz;
using Flow.Grains.Services.PlanItemBehaviorConfigurator;
using Flow.Grains.Services.PlanItemStateMachineConfigurator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NodaTime;
using NodaTime.Extensions;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.TestingHost;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Exceptions;
using Xunit;

namespace Flow.Grains.Tests.Integration.SiloFixture
{
    public class ClusterFixture : IDisposable, IAsyncLifetime
    {
        public TestCluster Cluster { get; private set; }
        public IClusterClient ClusterClient { get; private set; }

        private bool _disposed = false;

        public Task InitializeAsync()
        {
            var builder = new TestClusterBuilder();

            builder.Options.ClusterId = "integration";
            builder.Options.ServiceId = "Case.Flow";

            builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
            builder.AddClientBuilderConfigurator<TestClientConfigurator>();

            Cluster = builder.Build();
            Cluster.Deploy();

            ClusterClient = Cluster.Client;

            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            Cluster.StopAllSilos();
            return Task.CompletedTask;
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

        // Same predicate + settings the production silo registers (Flow.Silo/Program.cs): the
        // XSD-generated CMMN model can't reasonably get member [Id]s, and Newtonsoft JToken is used
        // internally by the Jint expression bridge. Both the silo and the test client below need
        // this registered, since a TestCluster's in-process client validates serializer coverage
        // independently. TypeNameHandling.Auto is required so the polymorphic CMMN model hierarchy
        // (PlanItemDefinition -> Stage/Milestone/HumanTask/...) round-trips as its concrete subtype
        // rather than silently collapsing to the statically-declared base type.
        private static bool IsFallbackSerializedType(Type type) =>
            (type.Namespace?.StartsWith("Flow.Grains.Interfaces.Model") ?? false) ||
            typeof(JToken).IsAssignableFrom(type);

        private static JsonSerializerSettings FallbackSerializerSettings() => new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Auto
        };

        // Mirrors Flow.Silo/Program.cs's ConfigureDevelopmentOrleans + serializer fallback, adapted
        // to the TestCluster's class-based ISiloConfigurator (TestClusterBuilder has no delegate
        // overload equivalent to ISiloHostBuilder's old ConfigureServices(HostBuilderContext, ...)).
        private class TestSiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder silo)
            {
                silo
                    .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)

                    .AddMemoryGrainStorageAsDefault() // grain state
                    .AddLogStorageBasedLogConsistencyProvider() // journaled grain
                    .AddMemoryGrainStorage("PubSubStore") // stream storage
                    .AddMemoryStreams("Default") // cluster stream provider
                    .UseInMemoryReminderService()

                    .ConfigureServices(ConfigureServices)
                    .ConfigureLogging(ConfigureLogging);

                silo.Services.AddSerializer(s => s.AddNewtonsoftJsonSerializer(
                    isSupported: IsFallbackSerializedType,
                    jsonSerializerSettings: FallbackSerializerSettings()));
            }

            private static void ConfigureServices(IServiceCollection services)
            {
                services
                    .AddSingleton<IClock>(SystemClock.Instance.InUtc())
                    .AddRuleExecutor()
                    .AddSingleton<IPlanItemBehaviorConfigurator, PlanItemBehaviorConfiguratorService>()
                    .AddSingleton<IPlanItemStateMachineConfigurator, PlanItemStateMachineConfiguratorService>()
                    .AddAutoMapper(cfg => cfg.AddProfile<CaseFlowProfile>(), Enumerable.Empty<Assembly>())
                    .AddQuartz(QuartzSchedulerConfig.Volatile);
            }

            private static void ConfigureLogging(ILoggingBuilder logging)
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
        }

        // TestCluster's in-process client independently validates serializer coverage for every
        // type reachable from grain interfaces, so it needs the same fallback registration as the
        // silo above - without this, client-side calls that touch CMMN model types throw
        // CodecNotFoundException even though the silo itself is configured correctly.
        private class TestClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.AddMemoryStreams("Default");

                clientBuilder.Services.AddSerializer(s => s.AddNewtonsoftJsonSerializer(
                    isSupported: IsFallbackSerializedType,
                    jsonSerializerSettings: FallbackSerializerSettings()));
            }
        }
    }
}
