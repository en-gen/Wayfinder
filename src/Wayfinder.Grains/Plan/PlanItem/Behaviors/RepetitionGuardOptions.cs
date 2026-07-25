namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    // ADO #67 - Case.Flow ENGINE EXTENSION, NOT CMMN 1.1 spec surface.
    // ~~~~~
    // 8.6.4 places no upper bound on RepetitionRule re-evaluation: a no-entry-criteria Stage or
    // Task instance with a constant-TRUE RepetitionRule is, per the letter of the spec, entitled
    // to spawn a new instance forever (BaseBehavior.TryRepeatOnCompleteOrTerminate re-evaluates
    // and re-publishes on every Complete/Terminate transition; StageBehavior/
    // CasePlanModelBehavior.HandleChildRepeated spawns unconditionally in response). That is
    // exactly the documented #19 foot-gun: a non-blocking Task (isBlocking=false) with
    // ManualActivationRule=false, RepetitionRule=TRUE, and no entry criteria auto-cascades
    // Create -> Available -> Active -> Complete -> repeat with no human or external event ever
    // entering the loop - spec-faithful, and exactly why it is dangerous to leave unbounded.
    //
    // Commercial CMMN engines uniformly add a repetition/loop ceiling as an ENGINE OPTION layered
    // on top of the spec (never as a spec deviation - the spec itself is silent on any limit,
    // so there is nothing in 8.6.4 to "conform" to here). This type mirrors that: it is consumed
    // by PlanItemBehaviorConfiguratorService (via IOptions<RepetitionGuardOptions>) and threaded
    // into StageBehavior/CasePlanModelBehavior's constructor, where HandleChildRepeated enforces
    // it before ever spawning a repeated child instance. Do not cite this ceiling, or a case that
    // hits it, in any CMMN conformance claim - hitting it is an engine safety net catching a
    // modeler mistake, not spec behavior and not an engine bug.
    public class RepetitionGuardOptions
    {
        public const string ConfigKey = "Engine:RepetitionGuard";

        // Generous default: legitimate CMMN models rarely repeat a single plan item anywhere
        // close to this many times (repetition is normally bounded by human work, a finite data
        // set, or a business-meaningful loop count), so 10,000 is large enough to never engage
        // during ordinary operation, while still bounding a runaway loop to a finite, recoverable
        // number of grain activations/stream subscriptions instead of truly unbounded growth.
        public const int DefaultMaxRepetitionsPerPlanItem = 10_000;

        // Max number of instances StageBehavior/CasePlanModelBehavior.HandleChildRepeated will
        // create for a single repeating plan item (i.e. repetition indices 0..MaxRepetitionsPerPlanItem-1)
        // before refusing the next spawn and driving the containing Stage/CasePlanModel to Fault.
        public int MaxRepetitionsPerPlanItem { get; set; } = DefaultMaxRepetitionsPerPlanItem;
    }
}
