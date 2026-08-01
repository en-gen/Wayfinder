using System;
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
        public void VerifyConfigurationShapes()
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
        // TO GET FIRING COST ALONE: subtract ConstructStageOrTask's reported mean from this
        // benchmark's reported mean. Both build the identical HumanTask machine shape, so that
        // subtraction isolates the cost of the four FireAsync calls. Do not read this benchmark's
        // raw number as "firing cost" on its own - it is construction + firing, and quoting it
        // unsubtracted overstates firing by the entire construction cost measured above.
        [Benchmark(Description = "Construct + fire Create->Enable->ManualStart->Complete (HumanTask)")]
        public IPlanItemStateMachine ConstructAndFireLifecycle()
        {
            IPlanItemStateMachine machine = NewHumanTaskMachine();

            machine.FireAsync(PlanItemTransition.Create).GetAwaiter().GetResult();
            machine.FireAsync(PlanItemTransition.Enable).GetAwaiter().GetResult();
            machine.FireAsync(PlanItemTransition.ManualStart).GetAwaiter().GetResult();
            machine.FireAsync(PlanItemTransition.Complete).GetAwaiter().GetResult();

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
