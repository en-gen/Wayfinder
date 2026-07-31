using System;
using System.Collections.Concurrent;
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
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Scheduler
{
    // Regression test for #197.
    //
    // The bug: QuartzSchedulerConfig.Volatile hardcoded quartz.scheduler.instanceName to
    // "UnitTest" for every caller. QuartzSchedulerFactory extends Quartz's own
    // StdSchedulerFactory, whose GetScheduler() resolves through Quartz's PROCESS-WIDE static
    // SchedulerRepository, keyed on that instanceName - a layer underneath (and invisible to)
    // each caller's own per-cluster DI singleton ISchedulerFactory registration.
    //
    // The actual mechanism this test guards against (see QuartzSchedulerConfig's remarks for the
    // full write-up, verified against Quartz 3.18.2 by instrumenting the repository directly -
    // this is NOT a disposal/shutdown race): GetScheduler() only runs
    // QuartzSchedulerFactory.Instantiate (which wires qs.JobFactory = the caller's own
    // IJobFactory) the FIRST time a scheduler is created under a given instanceName. Two
    // independently-lifecycled TestClusters sharing that name resolve the exact same IScheduler
    // object, but it stays wired to the FIRST cluster's JobFactory - so the SECOND cluster's
    // scheduled jobs still execute through the FIRST cluster's IClusterClient, publishing ticks
    // into the wrong cluster's stream space (or, once the first cluster has since disposed,
    // executing against its already-torn-down DI container). Nothing here ever calls
    // scheduler.Shutdown() - #31 removed the only call site, QuartzSchedulerFactory isn't
    // IDisposable, and no IScheduler is registered in DI - so a disposed cluster's scheduler is
    // orphaned ALIVE in the repository, not shut down.
    //
    // Why this test doesn't rely on xUnit collection ordering: xUnit does not document or
    // guarantee an execution order across collections (this assembly's
    // parallelizeTestCollections: false in xunit.runner.json exists only to stop 4 TestClusters'
    // silos fighting over 2 CPUs - see its own remarks - it is not an ordering guarantee, and
    // relying on one anyway is exactly the kind of implicit, easy-to-break coupling that let
    // #197 hide for as long as it did). Instead this test reproduces the collision's actual
    // mechanism directly, deterministically, inside a single test method: build and deploy
    // cluster A, prove a timer ticks on it, dispose it, then build and deploy an entirely
    // separate cluster B in the SAME process and prove ITS timer still ticks. Before #197's fix
    // (both using the shared "UnitTest" instanceName) cluster B's scheduler would resolve to the
    // same object as cluster A's - permanently wired to cluster A's (by then disposed)
    // JobFactory - and this assertion would fail; after the fix (each cluster fixture computes
    // its own unique instanceName) they never share a scheduler in the first place.
    //
    // ClusterAConfigurator/ClusterBConfigurator below deliberately do NOT reuse
    // SiloFixture.ClusterFixture or RepetitionGuardClusterFixture: those two are xUnit
    // ICollectionFixture singletons, built exactly once per test process and reused by every
    // other test in their collection. Reusing either type here (even to build a second, throwaway
    // instance) would mean this test's teardown touches the SAME Quartz instanceName (each
    // fixture type's name is itself only computed once, at type-initialization) that
    // TimerEventSchedulerGrainTests and friends depend on for the rest of the run - the very
    // outage this test exists to guard against, self-inflicted. So this test brings its own
    // minimal, separate pair of silo configurators, each with its own unique Quartz
    // instanceName, mirroring SiloFixture.ClusterFixture's TestSiloConfigurator (see its remarks
    // for the full rationale behind each registration) trimmed to only what
    // ITimerEventSchedulerGrain/TimerTickJob need.
    public class QuartzSchedulerInstanceNameCollisionTests
    {
        [Fact]
        public async Task DisposingOneClusterMustNotStopATimerOnAnIndependentlyLifecycledCluster()
        {
            var clusterA = new ThrowawayQuartzCluster<ClusterAConfigurator>(ClusterAConfigurator.QuartzInstanceName);
            try
            {
                await clusterA.InitializeAsync();
                await AssertTimerTicksAsync(clusterA.ClusterClient);
            }
            finally
            {
                // Cluster A disposing does NOT shut down its Quartz scheduler (nothing in this
                // codebase's Quartz wiring does - see QuartzSchedulerConfig's remarks); it just
                // tears down cluster A's own DI container/IClusterClient while the scheduler
                // itself is orphaned alive in Quartz's static repository under
                // ClusterAConfigurator.QuartzInstanceName. That is exactly what #197 needs pre-fix
                // sharing to bite: a second cluster resolving the SAME name would get this
                // now-orphaned scheduler, still wired to cluster A's dead JobFactory.
                await clusterA.DisposeAsync();
            }

            var clusterB = new ThrowawayQuartzCluster<ClusterBConfigurator>(ClusterBConfigurator.QuartzInstanceName);
            try
            {
                await clusterB.InitializeAsync();

                // Pre-#197 fix, this is where the bug bit: with both clusters configured under the
                // same shared instanceName, cluster B's ISchedulerFactory.GetScheduler() would
                // resolve the exact same IScheduler object cluster A created - still wired to
                // cluster A's JobFactory (and now cluster A's disposed IClusterClient) - so the
                // timer below would schedule successfully but its ticks would never reach cluster
                // B's subscriber, observed as exactly 0 ticks.
                await AssertTimerTicksAsync(clusterB.ClusterClient);
            }
            finally
            {
                await clusterB.DisposeAsync();
            }

            // Both DisposeAsync calls above must have actually removed their scheduler from
            // Quartz's static repository (Shutdown + SchedulerRepository.Remove), not just
            // disposed the owning TestCluster - otherwise every run of this test leaks a live
            // scheduler (and its thread pool) into the rest of the assembly's process.
            Quartz.Impl.SchedulerRepository.Instance.Lookup(ClusterAConfigurator.QuartzInstanceName)
                .Should().BeNull("cluster A's scheduler must be removed from Quartz's repository on dispose, not orphaned alive");
            Quartz.Impl.SchedulerRepository.Instance.Lookup(ClusterBConfigurator.QuartzInstanceName)
                .Should().BeNull("cluster B's scheduler must be removed from Quartz's repository on dispose, not orphaned alive");
        }

        private static async Task AssertTimerTicksAsync(IClusterClient clusterClient)
        {
            var caseInstanceId = Guid.NewGuid();
            var planItemInstanceId = ShortGuid.NewGuid();
            var expectedTicks = 2;
            // ConcurrentBag, not List: ticks are added from the stream subscription's callback
            // (a different execution context from the poll loop below reading .Count) - a plain
            // List<T> is not safe for concurrent add/read.
            var ticks = new ConcurrentBag<DateTimeOffset>();

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
                "the cluster's own Quartz scheduler must have run these jobs - if this cluster's " +
                "instanceName instead resolved another (possibly disposed) cluster's scheduler " +
                "from Quartz's shared static repository (issue #197), ticks would publish into " +
                "that other cluster's stream space instead of arriving here");
        }

        // Minimal TestCluster wrapper: builds, deploys, and tears down a throwaway cluster wired
        // with just enough (memory grain storage, in-memory reminders, memory streams, Quartz)
        // for ITimerEventSchedulerGrain/TimerTickJob to run, mirroring
        // SiloFixture.ClusterFixture.TestSiloConfigurator's registrations.
        private sealed class ThrowawayQuartzCluster<TConfigurator>
            where TConfigurator : ISiloConfigurator, new()
        {
            private readonly string _quartzInstanceName;

            private TestCluster _cluster;

            public ThrowawayQuartzCluster(string quartzInstanceName)
            {
                _quartzInstanceName = quartzInstanceName;
            }

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

            public async Task DisposeAsync()
            {
                _cluster?.StopAllSilos();

                // #197 follow-up: StopAllSilos() only tears down this cluster's own silo/DI
                // container - it does not touch Quartz's own process-wide static
                // SchedulerRepository (see QuartzSchedulerConfig's remarks: nothing in this
                // codebase's Quartz wiring is IDisposable, and no IScheduler is registered in DI).
                // Left alone, this cluster's scheduler would stay orphaned ALIVE - IsShutdown
                // false, its thread pool still running - under _quartzInstanceName for the rest of
                // the test process, which both leaks a live thread pool per test run and (had this
                // test's instanceName ever collided with another fixture's) would itself become a
                // source of exactly the bug this test exists to catch. Shut it down and remove it
                // from the repository explicitly so this regression test cannot leak state into
                // the rest of the suite.
                var scheduler = Quartz.Impl.SchedulerRepository.Instance.Lookup(_quartzInstanceName);
                if (scheduler != null)
                {
                    await scheduler.Shutdown(waitForJobsToComplete: true);
                }

                Quartz.Impl.SchedulerRepository.Instance.Remove(_quartzInstanceName);
            }
        }

        private class ClusterAConfigurator : ISiloConfigurator
        {
            // #197 - unique to THIS configurator type/cluster; see the class remarks above and
            // QuartzSchedulerConfig's remarks for why each independently-lifecycled cluster needs
            // its own instanceName. Internal (not private) so the outer test class can pass the
            // same value into ThrowawayQuartzCluster's constructor for teardown.
            internal static readonly string QuartzInstanceName =
                $"{nameof(QuartzSchedulerInstanceNameCollisionTests)}.A-{Guid.NewGuid():N}";

            public void Configure(ISiloBuilder silo) => ConfigureMinimalSilo(silo, QuartzInstanceName);
        }

        private class ClusterBConfigurator : ISiloConfigurator
        {
            internal static readonly string QuartzInstanceName =
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
