using System;
using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.Case;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using NodaTime;
using Orleans;
using Orleans.Streams;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Scheduler
{
    // #182 (sub-claim 2) fix - graduated from Scheduler/Repro/Issue182_SuspendedTimerTickDroppedIntegrationTests.cs
    // (repro history: CONFIRMED 3/3, see #182's comment thread). That repro test proved the DEFECT
    // by asserting the listener stayed permanently stuck in Available; these are its post-fix
    // replacements, asserting the CORRECT behavior: a tick witnessed while Suspended is buffered
    // (TimerEventListenerBehaviorStore.PendingSuspendedTicks, via SuspendedTimerTickBuffered) and
    // replayed on Resume (TimerEventListenerBehavior.HandleEnterAvailableFromResume) - see docs
    // section 2 ("suspension preserves state; it never discards it").
    //
    // Test design note (post-review): the original version of these tests waited on an INDEPENDENT
    // stream observer to witness a REAL Quartz-scheduled tick (a single-shot trigger ~10s out) as
    // its positive precondition. Under full-suite contention that observation can be starved
    // outright (Quartz's own thread pool never gets scheduled within the observation window - not
    // a flake, a deterministic denial under load). Since the fix under test is what
    // TimerEventListenerBehavior.ProcessTick does with a tick that ARRIVES while Suspended, not
    // whether Quartz manages to fire one under contention, these versions publish a SYNTHETIC
    // TimerTickedEvent directly onto the same stream identity TimerTickJob publishes to (confirmed
    // against Scheduler/TimerTickJob.cs) once the listener is confirmed genuinely Suspended -
    // exercising the exact same subscription path deterministically. The real schedule is pushed a
    // full hour out purely so Quartz never fires a second, untracked tick during the run; the
    // Suspend/Resume/Terminate cascades themselves stay real (fast in-process async messaging, not
    // wall-clock dependent, so no risk of the same starvation class).
    [Collection(ClusterCollection.Name)]
    public class SuspendedTimerTickIntegrationTests
    {
        private const string Scope = "CPM";

        private readonly IClusterClient _clusterClient;

        public SuspendedTimerTickIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000004");
        }

        [Fact]
        public async Task TimerEventListener__Given_TickArrivesWhileSuspended__When_LaterResumed__Then_BufferedTickReplaysExactlyOnceAndListenerCompletes()
        {
            const string timerDefinitionId = "SuspendableTimer";
            const string timerPlanItemId = "PlanItemTimer";

            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            var timerGrainInfo = await CreateSuspendableTimerCase(caseDefinitionId, caseInstanceId, timerDefinitionId, timerPlanItemId);
            var (caseGrain, timerGrain, timerInstanceId, tickStream) = timerGrainInfo;

            (await timerGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available,
                "the timer must be waiting on its schedule before we suspend it, or this test proves nothing");

            var caseAfterSuspend = await caseGrain.Trigger(PlanItemTransition.Suspend);
            caseAfterSuspend.PlanItemState.Should().Be(PlanItemState.Suspended, "Table 8.6 suspend: Active -> Suspended");

            var suspended = await PollUntil(
                async () => (await timerGrain.GetSnapshot()).PlanItemState == PlanItemState.Suspended,
                TimeSpan.FromSeconds(8));
            suspended.Should().BeTrue("the Suspend cascade must reach the timer before we publish the synthetic tick, or this test proves nothing");

            // Drive the tick directly while genuinely Suspended - see class remarks. No dependency
            // on Quartz's own thread pool getting scheduled under load.
            await tickStream.OnNextAsync(new TimerTickedEvent(null, null, DateTimeOffset.UtcNow, null));

            (await timerGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Suspended,
                "publishing the synthetic tick must not itself change state - it should only be " +
                "durably buffered (TimerEventListenerBehaviorStore.PendingSuspendedTicks), not acted " +
                "on, while still Suspended");

            // Resume - CasePlanModel leaves Suspended via Reactivate (Table 8.6/#63 D8 carve-out),
            // which EventListenerBehavior.HandleParentTransitioned maps to the child's own Resume
            // trigger (Suspended -[Resume]-> Available), which is exactly where
            // TimerEventListenerBehavior.HandleEnterAvailableFromResume replays the buffered tick.
            await caseGrain.Trigger(PlanItemTransition.Reactivate);

            // THE #182 FIX ASSERTION: a genuine positive-poll budget for the buffered tick's
            // replay. The underlying grain turn that processes Resume is non-reentrant and
            // completes the whole replay (Suspended -> Available -> Occur -> Completed) before any
            // external GetSnapshot() can interleave and observe an intermediate Available state -
            // so this positively waits for the DECISIVE terminal signal (Completed) rather than
            // checking for a transient Available window first. This poll is grounded in local
            // grain-turn completion, not an external clock, so it is not subject to the same
            // starvation class as waiting on Quartz.
            var reachedCompleted = await PollUntil(
                async () => (await timerGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed,
                TimeSpan.FromSeconds(30));

            reachedCompleted.Should().BeTrue(
                "the tick buffered while Suspended must be replayed on Resume - the fix's " +
                "TimerEventListenerBehavior.HandleEnterAvailableFromResume replays " +
                "TimerEventListenerBehaviorStore.PendingSuspendedTicks, firing the Occur transition " +
                "the synthetic tick never got to fire while the listener was Suspended");

            // "Replays exactly once": the ONLY correctness risk is double-processing the one
            // buffered tick into a second spawned instance (see BaseBehavior.
            // HasExplicitRepetitionRule's remarks - no rule attached here, so the no-rule branch
            // would otherwise republish unconditionally if ProcessTick were somehow invoked twice
            // for the same tick). Asserting exactly one child instance ever existed for this
            // PlanItem directly rules that out.
            var finalCaseSnapshot = await caseGrain.GetSnapshot();
            var timerInstances = finalCaseSnapshot.BehaviorExtension.Children[timerPlanItemId];
            timerInstances.Should().HaveCount(1,
                "the buffered tick must replay exactly once - no second (respawned) timer instance " +
                "should ever have been created from the one buffered tick");
            timerInstances.Should().ContainKey(timerInstanceId);
        }

        [Fact]
        public async Task TimerEventListener__Given_TickArrivesWhileSuspended__When_TerminatedInsteadOfResumed__Then_BufferedTickNeverReplays()
        {
            const string timerDefinitionId = "SuspendableTimerTerminated";
            const string timerPlanItemId = "PlanItemTimer";

            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";

            var (caseGrain, timerGrain, _, tickStream) = await CreateSuspendableTimerCase(caseDefinitionId, caseInstanceId, timerDefinitionId, timerPlanItemId);

            await caseGrain.Trigger(PlanItemTransition.Suspend);

            var suspended = await PollUntil(
                async () => (await timerGrain.GetSnapshot()).PlanItemState == PlanItemState.Suspended,
                TimeSpan.FromSeconds(8));
            suspended.Should().BeTrue("the Suspend cascade must reach the timer before we publish the synthetic tick, or this test proves nothing");

            await tickStream.OnNextAsync(new TimerTickedEvent(null, null, DateTimeOffset.UtcNow, null));

            (await timerGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Suspended,
                "the synthetic tick must have been buffered, not acted on, while still Suspended");

            // Terminate DIRECTLY instead of resuming - ConfigureForMilestoneOrEventListener permits
            // Suspended -[ParentTerminate]-> Terminated (Table 8.9's "termination cascades to
            // everything", docs section 3). This is the adversarial half of the fix's own
            // self-review: TimerEventListenerBehavior.HandleEnterAvailableFromResume is wired ONLY
            // to the Resume entry action, so it structurally never runs on this path - the buffered
            // tick must stay inert, not spuriously replay into a Completed transition on a
            // terminated listener.
            var afterTerminate = await timerGrain.Trigger(PlanItemTransition.ParentTerminate);
            afterTerminate.PlanItemState.Should().Be(PlanItemState.Terminated,
                "ConfigureForMilestoneOrEventListener permits ParentTerminate directly from Suspended");

            // Positive-but-bounded settle window: Terminated has no outgoing transition in
            // ConfigureForMilestoneOrEventListener, so there is nothing further for this listener to
            // ever do. Sampled repeatedly rather than a single check-after-sleep.
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
                (await timerGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Terminated,
                    "the buffered tick must never replay once the listener is Terminated instead of " +
                    "Resumed - HandleEnterAvailableFromResume only runs as an entry action reached via " +
                    "this item's own Resume trigger, which Terminated bypasses entirely");
            }
        }

        private async Task<(ICaseGrain CaseGrain, IPlanItemInternalGrain TimerGrain, string TimerInstanceId, IAsyncStream<TimerTickedEvent> TickStream)> CreateSuspendableTimerCase(
            string caseDefinitionId, Guid caseInstanceId, string timerDefinitionId, string timerPlanItemId)
        {
            // Real but genuinely-never-fires-during-this-test schedule (single shot, one hour out)
            // - the plan item still needs a valid TimerExpression to reach Available and schedule a
            // real Quartz trigger the normal way; the tick this test actually exercises is
            // published synthetically below, so the real trigger must stay silent for the whole run.
            var timerDefinition = new TimerEventListener
            {
                Id = timerDefinitionId,
                TimerExpression = Timers.TimerExpression(SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromHours(1)))
            };

            var @case = new Interfaces.Model.Case
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = Scope,
                    PlanItemDefinitions = { timerDefinition },
                    PlanItems =
                    {
                        new Interfaces.Model.PlanItem { Id = timerPlanItemId, DefinitionRef = timerDefinition.Id }
                    }
                }
            };

            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .Define(@case);

            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, Scope);

            await caseGrain.Create(caseDefinitionId);
            var caseSnapshot = await caseGrain.Trigger(PlanItemTransition.Create);

            var timerInstanceId = caseSnapshot.BehaviorExtension.Children[timerPlanItemId].Keys.Single();
            var timerGrain = _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{Scope}.{timerInstanceId}");

            var tickStream = _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<TimerTickedEvent>(caseInstanceId, timerInstanceId);

            return (caseGrain, timerGrain, timerInstanceId, tickStream);
        }

        private static async Task<bool> PollUntil(Func<Task<bool>> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await condition()) return true;
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            return await condition();
        }
    }
}
