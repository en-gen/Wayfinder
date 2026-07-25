using System;
using System.Net;
using System.Threading.Tasks;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Infrastructure.Quartz;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Services.PlanItemBehaviorConfigurator;
using Flow.Grains.Services.PlanItemStateMachineConfigurator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Extensions;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Exceptions;
using Xunit;

namespace Flow.Grains.Tests.Integration.Plan.CasePlanModel.RepetitionGuard
{
    // ADO #67 - a dedicated, isolated TestCluster (in-memory, no containers - as cheap as
    // SiloFixture.ClusterFixture) that pins RepetitionGuardOptions.MaxRepetitionsPerPlanItem to a
    // small value instead of the generous engine default. Deliberately NOT the shared
    // ClusterFixture/ClusterCollection: that singleton is reused by every other integration test
    // (including RepetitionOnCompletionIntegrationTests/SentryRepetitionResetIntegrationTests,
    // whose low repeat counts must keep exercising the real generous default, unperturbed), and
    // IPlanItemBehaviorConfigurator resolves its ceiling once via constructor injection - there is
    // no per-test override mechanism within a single shared cluster. Mirrors
    // SiloFixture.ClusterFixture byte-for-byte except the one added Configure<RepetitionGuardOptions>
    // call and a distinct ClusterId (avoids any ambiguity with the shared cluster, though
    // TestCluster instances do not actually collide).
    public class RepetitionGuardClusterFixture : IDisposable, IAsyncLifetime
    {
        // Small enough to assert on deterministically and quickly, large enough to distinguish
        // "the ceiling fired" from "something else stopped it after one repetition".
        public const int LowCeiling = 5;

        public TestCluster Cluster { get; private set; }
        public IClusterClient ClusterClient { get; private set; }

        private bool _disposed = false;

        public Task InitializeAsync()
        {
            var builder = new TestClusterBuilder();

            builder.Options.ClusterId = "repetition-guard-integration";
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

        // Mirrors SiloFixture.ClusterFixture.TestSiloConfigurator - see there for the full
        // rationale on each registration. The only substantive addition is the
        // Configure<RepetitionGuardOptions> call in ConfigureServices below.
        private class TestSiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder silo)
            {
                silo
                    .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)

                    .AddMemoryGrainStorageAsDefault(ConfigureMemoryStorage) // grain state
                    .AddLogStorageBasedLogConsistencyProvider() // journaled grain
                    .AddMemoryGrainStorage("PubSubStore", ConfigureMemoryStorage) // stream storage
                    .AddMemoryStreams("Default") // cluster stream provider
                    .UseInMemoryReminderService()

                    .ConfigureServices(ConfigureServices)
                    .ConfigureLogging(ConfigureLogging);

                silo.Services.AddSerializer(s => s.AddJsonSerializer(
                    isSupported: OrleansFallbackJsonSerializer.IsSupportedType,
                    jsonSerializerOptions: OrleansFallbackJsonSerializer.Options()));
            }

            private static void ConfigureMemoryStorage(OptionsBuilder<MemoryGrainStorageOptions> options) =>
                options.Configure<Serializer>((storageOptions, serializer) =>
                    storageOptions.GrainStorageSerializer = new OrleansGrainStorageSerializer(serializer));

            private static void ConfigureServices(IServiceCollection services)
            {
                services
                    .AddSingleton<IClock>(SystemClock.Instance.InUtc())
                    .AddRuleExecutor()
                    // ADO #67 - the one deliberate difference from SiloFixture.ClusterFixture:
                    // pins the repetition ceiling low so the foot-gun scenario (#19: non-blocking
                    // Task, ManualActivationRule=false, RepetitionRule=TRUE, no entry criteria)
                    // halts at a small, assertable count instead of spawning until the generous
                    // production default (10,000).
                    .Configure<RepetitionGuardOptions>(o => o.MaxRepetitionsPerPlanItem = LowCeiling)
                    .AddSingleton<IPlanItemBehaviorConfigurator, PlanItemBehaviorConfiguratorService>()
                    .AddSingleton<IPlanItemStateMachineConfigurator, PlanItemStateMachineConfiguratorService>()
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

        private class TestClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.AddMemoryStreams("Default");

                clientBuilder.Services.AddSerializer(s => s.AddJsonSerializer(
                    isSupported: OrleansFallbackJsonSerializer.IsSupportedType,
                    jsonSerializerOptions: OrleansFallbackJsonSerializer.Options()));
            }
        }
    }
}
