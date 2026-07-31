using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Infrastructure.Quartz;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Scheduler;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Text;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.TestingHost;
using Serilog;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Scheduler
{
    // Regression test for #197.
    //
    // The bug: QuartzSchedulerConfig.Volatile hardcoded quartz.scheduler.instanceName to
    // "UnitTest" for every caller. QuartzSchedulerFactory extends Quartz's own
    // StdSchedulerFactory, whose GetScheduler() resolves through Quartz's PROCESS-WIDE static
    // SchedulerRepository, keyed on that instanceName - a layer underneath (and invisible to)
    // each caller's own per-cluster DI singleton ISchedulerFactory registration. Two
    // independently-lifecycled TestClusters sharing that name shared the exact same underlying
    // Quartz IScheduler; when the first cluster disposed, it shut that shared scheduler down
    // (Quartz schedulers cannot be restarted once shut down), and the second cluster then
    // silently resolved the same, already-dead scheduler - observed as exactly 0 ticks on
    // whichever timer test happened to run in a later collection.
    //
    // Why this test doesn't rely on xUnit collection ordering: xUnit does not document or
    // guarantee an execution order across collections (this assembly's
    // parallelizeTestCollections: false in xunit.runner.json exists only to stop 4 TestClusters'
    // silos fighting over 2 CPUs - see its own remarks - it is not an ordering guarantee, and
    // relying on one anyway is exactly the kind of implicit, easy-to-break coupling that let
    // #197 hide for as long as it did). Instead this test reproduces the collision's actual
    // mechanism directly, deterministically, inside a single test method: build and deploy
    // cluster A, prove a timer ticks on it, dispose it (shutting its Quartz scheduler down),
    // then build and deploy an entirely separate cluster B in the SAME process and prove ITS
    // timer still ticks. Before #197's fix (both using the shared "UnitTest" instanceName) this
    // assertion would fail - cluster B would resolve cluster A's shut-down scheduler and observe
    // 0 ticks; after the fix (each cluster fixture computes its own unique instanceName) they
    // never collide.
    //
    // ClusterFixtureA/ClusterFixtureB below deliberately do NOT reuse SiloFixture.ClusterFixture
    // or RepetitionGuardClusterFixture: those two are xUnit ICollectionFixture singletons, built
    // exactly once per test process and reused by every other test in their collection.
    // Reusing either type here (even to build a second, throwaway instance) would mean this
    // test's Dispose call tears down the SAME Quartz instanceName (each fixture type's name is
    // itself only computed once, at type-initialization) that TimerEventSchedulerGrainTests and
    // friends depend on for the rest of the run - the very outage this test exists to guard
    // against, self-inflicted. So this test brings its own minimal, separate pair of silo
    // configurators, each with its own unique Quartz instanceName, mirroring
    // SiloFixture.ClusterFixture's TestSiloConfigurator (see its remarks for the full rationale
    // behind each registration) trimmed to only what ITimerEventSchedulerGrain/TimerTickJob need.
    public class QuartzSchedulerInstanceNameCollisionTests
    {
        [Fact]
        public async Task DisposingOneClusterMustNotStopATimerOnAnIndependentlyLifecycledCluster()
        {
            var clusterA = new ThrowawayQuartzCluster<ClusterAConfigurator>();
            try
            {
                await clusterA.InitializeAsync();
                await AssertTimerTicksAsync(clusterA.ClusterClient);
            }
            finally
            {
                // Mirrors the exact moment #197 describes: a TestCluster disposing (and, via it,
                // shutting down its Quartz scheduler) while another, independently-lifecycled
                // Quartz-using cluster is about to start.
                await clusterA.DisposeAsync();
            }

            var clusterB = new ThrowawayQuartzCluster<ClusterBConfigurator>();
            try
            {
                await clusterB.InitializeAsync();

                // Pre-#197 fix, this is where the bug bit: cluster B's ISchedulerFactory
                // .GetScheduler() would resolve cluster A's already-shut-down scheduler from
                // Quartz's static SchedulerRepository (both configured with instanceName
                // "UnitTest"), and the timer below would observe exactly 0 ticks.
                await AssertTimerTicksAsync(clusterB.ClusterClient);
            }
            finally
            {
                await clusterB.DisposeAsync();
            }
        }

        private static async Task AssertTimerTicksAsync(IClusterClient clusterClient)
        {
            var caseInstanceId = Guid.NewGuid();
            var planItemInstanceId = ShortGuid.NewGuid();
            var expectedTicks = 2;
            var ticks = new List<DateTimeOffset>();

            await clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<TimerTickedEvent>(caseInstanceId, (string)planItemInstanceId)
                .SubscribeAsync((@event, token) =>
                {
                    ticks.Add(@event.FireTime);
                    return Task.CompletedTask;
                });

            var period = Period.FromSeconds(1).Normalize();
            var isoPeriod = PeriodPattern.NormalizingIso.Format(period);

            await clusterClient.GetGrain<ITimerEventSchedulerGrain>(caseInstanceId)
                .ScheduleTimer(
                    planItemInstanceId,
                    new Iso8601($"R{expectedTicks - 1}/{isoPeriod}"),
                    DateTime.UtcNow,
                    new Dictionary<string, object>
                    {
                        ["CaseInstanceId"] = caseInstanceId,
                        ["ElementType"] = typeof(PlanItem).Name,
                        ["PlanItemDefinition"] = typeof(TimerEventListener).Name,
                        ["ElementScope"] = "CPM.ParentStage",
                        ["ElementInstanceId"] = (string)planItemInstanceId
                    });

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline && ticks.Count < expectedTicks)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            ticks.Should().HaveCountGreaterThanOrEqualTo(
                expectedTicks,
                "the cluster's Quartz scheduler must still be running - if it resolved another, " +
                "already-disposed cluster's shut-down scheduler (issue #197), no ticks would ever " +
                "arrive");
        }

        // Minimal TestCluster wrapper: builds, deploys, and tears down a throwaway cluster wired
        // with just enough (memory grain storage, in-memory reminders, memory streams, Quartz)
        // for ITimerEventSchedulerGrain/TimerTickJob to run, mirroring
        // SiloFixture.ClusterFixture.TestSiloConfigurator's registrations.
        private sealed class ThrowawayQuartzCluster<TConfigurator>
            where TConfigurator : ISiloConfigurator, new()
        {
            private TestCluster _cluster;

            public IClusterClient ClusterClient { get; private set; }

            public Task InitializeAsync()
            {
                var builder = new TestClusterBuilder();

                builder.Options.ClusterId = $"quartz-collision-{typeof(TConfigurator).Name}";
                builder.Options.ServiceId = "Wayfinder";

                builder.AddSiloBuilderConfigurator<TConfigurator>();
                builder.AddClientBuilderConfigurator<MinimalClientConfigurator>();

                _cluster = builder.Build();
                _cluster.Deploy();

                ClusterClient = _cluster.Client;

                return Task.CompletedTask;
            }

            public Task DisposeAsync()
            {
                _cluster?.StopAllSilos();
                Log.CloseAndFlush();
                return Task.CompletedTask;
            }
        }

        private class ClusterAConfigurator : ISiloConfigurator
        {
            // #197 - unique to THIS configurator type/cluster; see the class remarks above and
            // QuartzSchedulerConfig's remarks for why each independently-lifecycled cluster needs
            // its own instanceName.
            private static readonly string QuartzInstanceName =
                $"{nameof(QuartzSchedulerInstanceNameCollisionTests)}.A-{Guid.NewGuid():N}";

            public void Configure(ISiloBuilder silo) => ConfigureMinimalSilo(silo, QuartzInstanceName);
        }

        private class ClusterBConfigurator : ISiloConfigurator
        {
            private static readonly string QuartzInstanceName =
                $"{nameof(QuartzSchedulerInstanceNameCollisionTests)}.B-{Guid.NewGuid():N}";

            public void Configure(ISiloBuilder silo) => ConfigureMinimalSilo(silo, QuartzInstanceName);
        }

        // Mirrors SiloFixture.ClusterFixture.TestClientConfigurator: the TestCluster's in-process
        // client independently validates serializer coverage for every type reachable from grain
        // interfaces (e.g. the CMMN model types referenced by TimerEventSchedulerGrain's context
        // dictionary), so it needs the same fallback serializer/exception-namespace registration
        // as the silo. No Quartz involvement, so a single shared configurator type is fine for
        // both clusters here.
        private class MinimalClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.AddMemoryStreams("Default");

                clientBuilder.Services.AddSerializer(s => s.AddJsonSerializer(
                    isSupported: OrleansFallbackJsonSerializer.IsSupportedType,
                    jsonSerializerOptions: OrleansFallbackJsonSerializer.Options()));

                clientBuilder.Services.Configure<ExceptionSerializationOptions>(
                    options => options.SupportedNamespacePrefixes.Add("Wayfinder"));
            }
        }

        // Mirrors SiloFixture.ClusterFixture.TestSiloConfigurator.Configure - see there for the
        // full rationale on each registration - trimmed to what ITimerEventSchedulerGrain and
        // TimerTickJob actually touch (grain state storage, reminders, the "Default" stream
        // provider, and Quartz).
        private static void ConfigureMinimalSilo(ISiloBuilder silo, string quartzInstanceName)
        {
            silo
                .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                .AddMemoryGrainStorageAsDefault(ConfigureMemoryStorage)
                .AddMemoryGrainStorage("PubSubStore", ConfigureMemoryStorage)
                .AddMemoryStreams("Default")
                .UseInMemoryReminderService()
                .ConfigureServices(services => services.AddQuartz(QuartzSchedulerConfig.Volatile(quartzInstanceName)))
                .ConfigureLogging(IntegrationTestLogging.Configure);

            silo.Services.AddSerializer(s => s.AddJsonSerializer(
                isSupported: OrleansFallbackJsonSerializer.IsSupportedType,
                jsonSerializerOptions: OrleansFallbackJsonSerializer.Options()));

            silo.Services.Configure<ExceptionSerializationOptions>(
                options => options.SupportedNamespacePrefixes.Add("Wayfinder"));
        }

        private static void ConfigureMemoryStorage(Microsoft.Extensions.Options.OptionsBuilder<MemoryGrainStorageOptions> options) =>
            options.Configure<Serializer>((storageOptions, serializer) =>
                storageOptions.GrainStorageSerializer = new OrleansGrainStorageSerializer(serializer));
    }
}
