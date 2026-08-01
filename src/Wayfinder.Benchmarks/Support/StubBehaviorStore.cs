using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan;

namespace Wayfinder.Benchmarks.Support
{
    // A fixed IBehaviorStore for state-machine benchmarks, standing in for the PlanItemStore an
    // activated PlanItemGrain would supply. Three members actually steer
    // PlanItemStateMachine's construction (see that class's ctor,
    // src/Wayfinder.Grains/Plan/PlanItem/StateMachine/PlanItemStateMachine.cs):
    //
    //   - PlanItemDefinition, which ConfigureFor switches on to pick between the CasePlanModel,
    //     Stage/Task, and Milestone/EventListener Permit/PermitIf table. A null or wrongly-typed
    //     definition would fall through that switch with NO Permit calls registered at all,
    //     producing an empty state machine and a construction cost that understates the real one
    //     by most of its work. This is the invariant from the design spec's section 5.
    //   - PlanItemState, which becomes the machine's initial state (passed to the base
    //     StateMachine<TState,TTrigger> ctor).
    //   - ParentSuspendState, which the ctor copies directly into a property
    //     (`ParentSuspendState = planItemStore.ParentSuspendState;`) - unlike the two above it
    //     drives no Permit registration and costs nothing measurable, but it IS read by the state
    //     machine itself, not only by behaviors.
    //
    // The rest are read by behaviors, not by the state machine, and hold benign fixed values.
    public sealed class StubBehaviorStore : IBehaviorStore
    {
        public StubBehaviorStore(PlanItemDefinition definition, PlanItemState state = PlanItemState.Uninitialized)
        {
            PlanItemDefinition = definition;
            PlanItemState = state;
        }

        public bool Defined => true;

        public string CaseDefinitionId => "BenchmarkCase";

        public PlanItemDefinition PlanItemDefinition { get; }

        public bool UserCompletable => false;

        public bool Repeated => false;

        public int Repetition => 0;

        public PlanItemState PlanItemState { get; }

        public PlanItemState? ParentSuspendState => null;

        public object BehaviorExtension => null;
    }
}
