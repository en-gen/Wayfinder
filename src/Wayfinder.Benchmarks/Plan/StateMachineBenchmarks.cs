using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfinder.Benchmarks.Support;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.PlanItem.StateMachine;
using Wayfinder.Grains.Services.PlanItemStateMachineConfigurator;

namespace Wayfinder.Benchmarks.Plan
{
    // #224 / design spec section 4.3 - PlanItemStateMachine construction.
    //
    // PlanItemStateMachineConfiguratorService.Configure runs PlanItemStateMachine's ctor, which
    // runs ConfigureFor(PlanItemDefinition) - all the Stateless Permit/PermitIf registration for
    // one of three transition tables (see PlanItemStateMachine.ConfigureFor's switch). That
    // construction is paid once per PlanItemGrain ACTIVATION, not once per transition fired, so
    // at #34's target shape (10k cases x 50 plan items) it runs roughly 500,000 times. Construction
    // cost is therefore the number this class exists to measure - firing cost is secondary and
    // amortizes over however many transitions a plan item's lifetime actually fires.
    //
    // Logging caveat: ConfigureFor calls _logger.LogInformation("Initializing {BehaviorType} state
    // machine", ...) on every construction, unconditionally. These benchmarks build the machine
    // with NullLoggerFactory, so that call costs ~0 here. A production silo running with
    // Information-level logging enabled pays real message-template formatting and sink-write cost
    // on every one of those 500,000 activations, and that cost is NOT captured by any number
    // below - do not read these results as the full production per-activation cost.
    //
    // Behavior-callback caveat: production never fires a state machine straight out of
    // PlanItemStateMachineConfiguratorService. PlanItemBehaviorConfiguratorService.Configure hands
    // the freshly-configured machine to a behavior (e.g. TaskBehavior) whose constructor
    // immediately layers on its own Configure(Completed).OnEntryAsync(...),
    // .OnEntryFromAsync(Complete, ...), Configure(Terminated)..., OnTransitionedAsync,
    // OnUnhandledTriggerAsync, plus further per-type registrations - see that class. This
    // benchmark's machines are the bare Stateless result of
    // PlanItemStateMachineConfiguratorService.Configure alone: no behavior is ever layered on, so
    // no OnEntryAsync/OnTransitionedAsync callback ever runs, and none of the grain calls, event
    // journaling, or stream publishes those callbacks perform are part of any number below.
    // Consequently ConstructAndFireLifecycle (below) measures bare Stateless transition cost, not
    // plan-item lifecycle cost, and its own remarks restate this at the point a reader would
    // otherwise mistake one for the other. The cost of the missing callbacks is UNMEASURED here -
    // not small, not estimated, unmeasured - and design spec §4.3 deliberately scopes the Orleans
    // runtime out of this project, so this is a framing note, not a gap this class is meant to
    // close.
    [MemoryDiagnoser]
    public class StateMachineBenchmarks
    {
        private readonly PlanItemStateMachineConfiguratorService _configurator =
            new PlanItemStateMachineConfiguratorService(NullLoggerFactory.Instance);

        // #224 design spec section 5's correctness invariant: ConfigureFor's switch has no
        // default case, so a null or wrongly-typed PlanItemDefinition falls straight through it
        // registering ZERO Permit calls. The resulting machine still constructs successfully and
        // still reports a State - nothing throws - so an unconfigured machine is silent unless
        // something checks for it explicitly. It would produce a "construction cost" a small
        // fraction of the real one, and that wrong number is worse than no benchmark because
        // nobody would know to distrust it. GlobalSetup builds one machine of each shape and
        // verifies CanFire(Create) from Uninitialized before any benchmark method is allowed to
        // run, exactly so this can never ship silently broken.
        [GlobalSetup]
        public void GlobalSetup()
        {
            VerifyConfigured("CasePlanModel Stage", NewCasePlanModelMachine());
            VerifyConfigured("Stage/Task (HumanTask)", NewHumanTaskMachine());
            VerifyConfigured("Milestone/EventListener (Milestone)", NewMilestoneMachine());
        }

        private static void VerifyConfigured(string shapeName, IPlanItemStateMachine machine)
        {
            if (!machine.CanFire(PlanItemTransition.Create))
            {
                throw new InvalidOperationException(
                    $"{shapeName} state machine has no Permit(Create) registered from Uninitialized - " +
                    "ConfigureFor's switch fell through without configuring it. Benchmarking this " +
                    "machine would measure an empty configuration, not real construction cost.");
            }
        }

        // ConfigureForCasePlanModel() - the outermost Stage (IsCasePlanModel == true) branch.
        [Benchmark(Description = "Construct: CasePlanModel Stage")]
        public IPlanItemStateMachine ConstructCasePlanModel() => NewCasePlanModelMachine();

        // ConfigureForStageOrTask() - shared by ordinary (non-case-plan-model) Stage and every
        // BaseTask subtype. HumanTask is the common case in real case models.
        [Benchmark(Description = "Construct: Stage/Task (HumanTask)")]
        public IPlanItemStateMachine ConstructStageOrTask() => NewHumanTaskMachine();

        // ConfigureForMilestoneOrEventListener() - the smallest of the three tables.
        [Benchmark(Description = "Construct: Milestone/EventListener (Milestone)")]
        public IPlanItemStateMachine ConstructMilestoneOrEventListener() => NewMilestoneMachine();

        // Construction + full lifecycle firing, deliberately NOT split via [IterationSetup].
        //
        // Firing mutates the machine's state, so unlike the three Construct* benchmarks above a
        // single instance cannot be reused across invocations - each invocation needs its own
        // fresh machine. [IterationSetup] would give us that, but it runs outside the measured
        // region on a per-iteration (not per-operation) basis and distorts BenchmarkDotNet's
        // statistical model (see the design spec's rejection of that approach). So this method
        // instead measures construction AND firing together, as one number.
        //
        // TO GET BARE STATELESS TRANSITION COST: subtract ConstructStageOrTask's reported mean
        // from this benchmark's reported mean. Both build the identical HumanTask machine shape,
        // so that subtraction isolates the cost of the four FireAsync calls against a machine with
        // NO behavior attached (see the class-level behavior-callback caveat above) - it is bare
        // Stateless transition cost, NOT plan-item lifecycle cost, because production always fires
        // through a behavior-wrapped machine (PlanItemBehaviorConfiguratorService.Configure) whose
        // OnEntryAsync/OnTransitionedAsync callbacks do grain calls, event journaling, and stream
        // publishes that never run here. Likewise, the construction figure being subtracted is
        // only the state-machine half of per-activation configuration (Stateless Permit/PermitIf
        // registration), not the full cost of configuring a plan item for activation - the
        // behavior layer's own construction is not measured by this class. Do not read this
        // benchmark's raw number as "firing cost" on its own - it is construction + firing, and
        // quoting it unsubtracted overstates firing by the entire construction cost measured
        // above. The cost the missing behavior callbacks would add is UNMEASURED - not
        // estimated - see the class-level caveat.
        //
        // async Task<T>, not GetAwaiter().GetResult(): production code (Grains) always awaits
        // FireAsync directly - the sync-over-async pattern this method used to use appears nowhere
        // in that code path and risks a deadlock/thread-pool-starvation shape production never
        // exercises. BenchmarkDotNet natively times async Task<T> benchmark methods, so there is
        // no reason to block here.
        [Benchmark(Description = "Construct + fire Create->Enable->ManualStart->Complete (HumanTask)")]
        public async Task<IPlanItemStateMachine> ConstructAndFireLifecycle()
        {
            IPlanItemStateMachine machine = NewHumanTaskMachine();

            await machine.FireAsync(PlanItemTransition.Create);
            await machine.FireAsync(PlanItemTransition.Enable);
            await machine.FireAsync(PlanItemTransition.ManualStart);
            await machine.FireAsync(PlanItemTransition.Complete);

            if (machine.State != PlanItemState.Completed)
            {
                throw new InvalidOperationException(
                    $"Lifecycle sequence did not reach Completed (ended in {machine.State}) - " +
                    "the fired sequence no longer matches ConfigureForStageOrTask's transition table.");
            }

            return machine;
        }

        private IPlanItemStateMachine NewCasePlanModelMachine() =>
            _configurator.Configure(new StubBehaviorStore(new Stage { IsCasePlanModel = true }));

        private IPlanItemStateMachine NewHumanTaskMachine() =>
            _configurator.Configure(new StubBehaviorStore(new HumanTask()));

        private IPlanItemStateMachine NewMilestoneMachine() =>
            _configurator.Configure(new StubBehaviorStore(new Milestone()));
    }
}
