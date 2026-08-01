using System;
using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.PlanItem;
using Wayfinder.Grains.Plan.PlanItem.Behaviors.Stores;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Plan.PlanItem.StateMachine;
using Wayfinder.Grains.Plan.PlanningTable;
using Wayfinder.Grains.Plan.Sentry;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Wayfinder.Grains.Plan.PlanItem.Behaviors
{
    public class StageBehavior : BaseBehavior<Stage>
    {
        private StageBehaviorStore StageStore => Host.State.BehaviorExtension as StageBehaviorStore ?? throw new InvalidOperationException();

        // ADO #66 - override point for the CasePlanModel/ordinary-Stage-or-Task asymmetry
        // ~~~~~
        // Table 5.31's transition glossary scopes `exit` to "the Stage or Task" only (the
        // casePlanModel is deliberately omitted) and `terminate` to "the casePlanModel, Stage, or
        // Task". HandleSentrySatisfied below is shared verbatim by CasePlanModelBehavior, so this
        // is the one seam that lets the CasePlanModel fire a different trigger for the exact same
        // "my own exit criterion was satisfied" event, without duplicating the rest of the method.
        // Ordinary Stage/Task instances keep the base Exit; CasePlanModelBehavior overrides this to
        // Terminate (see PlanItemStateMachine.ConfigureForCasePlanModel's remarks for why Table 8.6
        // has no `exit` row for the casePlanModel to permit in the first place).
        protected virtual PlanItemTransition ExitCriterionTransition => PlanItemTransition.Exit;

        // ADO #67 - RepetitionGuardOptions.MaxRepetitionsPerPlanItem, threaded through from
        // PlanItemBehaviorConfiguratorService (IOptions<RepetitionGuardOptions>). Defaulted here
        // (rather than required) so every existing direct `new StageBehavior(...)` in the unit
        // test suite keeps compiling against the generous default without modification - only
        // production DI and tests specifically targeting the ceiling need to pass it explicitly.
        // See HandleChildRepeated for enforcement and RepetitionGuardOptions for the full
        // rationale (Wayfinder ENGINE EXTENSION, not CMMN spec surface).
        private readonly int _repetitionCeiling;

        public StageBehavior(
            IBehaviorHost host,
            Stage planItemDefinition,
            IPlanItemStateMachine stateMachine,
            int repetitionCeiling = RepetitionGuardOptions.DefaultMaxRepetitionsPerPlanItem) :
            base(host, planItemDefinition, stateMachine)
        {
            _repetitionCeiling = repetitionCeiling;

            StateMachine.Configure(PlanItemState.Available)
                .OnEntryFromAsync(PlanItemTransition.Create, HandleEnterAvailableFromCreate);

            StateMachine.Configure(PlanItemState.Active)
                .OnEntryFromAsync(PlanItemTransition.Start, HandleEnterActiveFromStart)
                .OnEntryFromAsync(PlanItemTransition.ManualStart, HandleEnterActiveFromStart)
                // #178 - replay any repetition requests HandleChildRepeated buffered while this
                // Host was genuinely Suspended (see that method's Suspended branch and
                // DrainPendingRepetitions below). Registered on exactly the two triggers that
                // land here FROM Suspended (Table 8.7/8.8's own Resume and the cascaded-suspend
                // counterpart ParentResume - see PlanItemStateMachine.ConfigureForStageOrTask),
                // not as an unconditional entry action on Active: Start/ManualStart's own arrival
                // is handled by HandleEnterActiveFromStart above, is never reachable FROM
                // Suspended (ConfigureForStageOrTask permits no such edge), and the buffer is
                // always empty on a first activation regardless.
                //
                // Deliberately NOT registered for Reactivate here (ordinary Stage/Task's only
                // source for Reactivate is Failed - see ConfigureForStageOrTask): a buffered
                // entry left over after DrainPendingRepetitions itself Faults this Host mid-batch
                // (the #67 ceiling - see that method's ceilingBreached remarks) is, by
                // construction, one that would immediately re-fault the container if replayed. A
                // human reactivating from Failed is a recovery action for whatever ORIGINALLY
                // faulted the Host, not a request to re-attempt every stale spawn that was queued
                // behind the one that caused it - auto-draining on Reactivate would silently
                // re-trigger that exact fault. This is a considered decision, not an oversight:
                // see CasePlanModelBehavior's own Reactivate registration for the CASE-level
                // counterpart of this same call (its Reactivate is overloaded across MULTIPLE
                // source states by Table 8.6, so it needs a source-state guard rather than simply
                // omitting the hook). #198 - because those entries are never replayed here, they
                // are settled where they are stranded (SettleStrandedPendingRepetitions), so this
                // decision cannot leave a reactivated container unable to complete.
                .OnEntryFromAsync(PlanItemTransition.Resume, DrainPendingRepetitions)
                .OnEntryFromAsync(PlanItemTransition.ParentResume, DrainPendingRepetitions);
        }

        public override Task Activate() =>
            Task.WhenAll(
                base.Activate(),
                Task.WhenAll(PlanItemDefinition.PlanItems.Select(planItem =>
                    Task.WhenAll(
                        Host.SubscribeTo<PlanItemTransitionedEvent>(
                            planItem.Id,
                            HandleChildTransitioned,
                            StreamFlags.Resume),
                        Host.SubscribeTo<PlanItemRepetitionCriteriaMetEvent>(
                            planItem.Id,
                            HandleChildRepeated,
                            StreamFlags.Resume)))));


        protected override Task Define() =>
            Task.WhenAll(
                // based on interpretation of 8.7 - Available, EntryCriterion may occur
                // before parent is Active, and therefor before this is available
                SubscribeToCriteria(x => x.EntryCriteria, StreamFlags.Create | StreamFlags.Resume),
                // define sentries
                Task.WhenAll(PlanItemDefinition.Sentries
                    .Select(async sentry =>
                    {
                        var sentryInstanceId = ShortGuid.NewGuid();

                        var sentryGrain = Host.GrainFactory
                            .GetGrain<ISentryGrain>(Host.CaseInstanceId, $"{Host.Address}.{sentryInstanceId}");

                        if (await sentryGrain.Defined()) return;

                        Host.LogWithContext(logger => logger.LogInformation(
                            "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | creating sentry {SentryId}",
                            Host.Definition.GetType().Name,
                            PlanItemDefinition.GetType().Name,
                            Host.Scope,
                            Host.InstanceId,
                            sentryInstanceId));

                        await sentryGrain.Define(Host.State.CaseDefinitionId, sentry);

                        // TODO: should raise an event to catalog sentry instance?
                    })),
                // define planning table
                DefinePlanningTable());

        private async Task DefinePlanningTable()
        {
            if (PlanItemDefinition.PlanningTable == null) return;

            var planningTableGrain = Host.GrainFactory
                .GetGrain<IPlanningTableGrain>(Host.CaseInstanceId, Host.InstanceId);

            if (await planningTableGrain.Defined()) return;

            await planningTableGrain.Define(Host.State.CaseDefinitionId, PlanItemDefinition.PlanningTable);
        }

        // 8.8 - Stage and Task instance transitions
        // ~~~~~
        // Create: Transition to the initial state (Available) when the Stage or Task
        // instance is created. This happens when the Stage instance
        // containing this Stage or Task instance transitions to Active. The
        // RepetitionRule Boolean expression MUST be evaluated in this
        // transition. The RequiredRule Boolean expression MUST be
        // evaluated in this transition, and its Boolean value SHOULD be
        // maintained for the rest of the life of the Stage or Task instance.
        // ==========
        // D6 - exit-criteria subscription on the create path
        // ~~~~~
        // 8.5: "Exit criterion sentries are considered ready for evaluation while the ... Stage ...
        // is in Active state." A Stage's ExitCriteria are only reachable (StateMachine.CanFire
        // (PlanItemTransition.Exit) from Available/Enabled/Active/etc. - see
        // PlanItemStateMachine.ConfigureForStageOrTask) once this create path has already run, so
        // there is no later "when Active" moment that separately arms the subscription - it MUST
        // happen here, same as EntryCriteria a few lines above and same as
        // TaskBehavior.HandleEnterAvailableFromCreate already does for its own ExitCriteria.
        // Before this fix, only base.Activate()'s Resume-only subscription
        // (BaseBehavior.Activate -> SubscribeToCriteria(x => x.ExitCriteria, StreamFlags.Resume))
        // ever touched this stream; CmmnElementGrain.SubscribeTo's Resume branch is a no-op unless a
        // subscription handle already exists (GetAllSubscriptionHandles().Any()), and nothing ever
        // created one - so a Stage's exit criteria were not merely delayed until a
        // deactivate/reactivate cycle, they were never subscribed at all. Unlike TaskBehavior's
        // ExitCriteria (gated by IsBlocking - a non-blocking Task completes immediately and 5.24
        // forbids it from declaring exitCriteriaRefs at all), Table 5.34 places no equivalent
        // condition on a Stage's ExitCriteria, so this subscription is unconditional.
        // Sequential, not Task.WhenAll (#19, found via the D7 e2e): interleaving
        // EvaluateRequiredRule's internal ConfirmEvents with EnableOrStart's nested
        // FireAsync(Enable) (whose HandleTransitioned raises + confirms again) on the same
        // journaled-grain adaptor intermittently NREs reading TentativeState mid-confirm -
        // observed as a racy "Object reference not set" inside HandleTransitioned's log action
        // during Trigger(Create), and, when the create arrives via a stream-delivery turn
        // (HandleChildRepeated -> CreateChild), swallowed silently by the streaming agent
        // (= Bug #62's missing repetitions). Rule evaluations stay ordered before any
        // transition cascade; subscriptions are armed before EnableOrStart so nothing the
        // cascade produces can be missed.
        private async Task HandleEnterAvailableFromCreate()
        {
            await EvaluateRepetitionRule(discard: true);
            await EvaluateRequiredRule();

            await SubscribeToCriteria(x => x.ExitCriteria, StreamFlags.Create);

            // 8.7 - Stage and Task instances states
            // ~~~~~
            // While available, the Stage or Task instance is waiting for its entry criteria (Sentry) to become TRUE.
            // A missing entry criteria(Sentry) is considered TRUE.
            if (Host.Definition.EntryCriteria.Any())
            {
                await SubscribeToCriteria(x => x.EntryCriteria, StreamFlags.Create);
            }
            else
            {
                await EnableOrStart();
            }
        }

        private async Task EnableOrStart()
        {
            if (await EvaluateManualActivationRule())
            {
                await StateMachine.FireAsync(PlanItemTransition.Enable);
            }
            else
            {
                await StateMachine.FireAsync(PlanItemTransition.Start);
            }
        }

        // Shared with CasePlanModelBehavior: 8.6.1 Stage semantics describe child PlanItem
        // instantiation as happening on entry to Active, regardless of which trigger reaches
        // Active (Start/ManualStart for an ordinary Stage; Create for the CasePlanModel's
        // outermost Stage - see Table 8.6/Table 5.31, and CasePlanModelBehavior's remarks).
        //
        // 5.4.9.2/8.7 - DiscretionaryItem / Planning
        // ~~~~~
        // Only PlanItemDefinition.PlanItems (the plan) are instantiated here.
        // PlanningTable.DiscretionaryItems are NOT instantiated on entry to Active: a
        // DiscretionaryItem instance is planned "to the discretion" of a Case worker, moved
        // into the plan only when a worker selects it from the PlanningTable at run-time
        // (8.7 Planning). Auto-instantiating them here would contradict that definition.
        //
        // ConfirmEvents() here (found necessary while wiring CasePlanModelBehavior, #55): Stateless
        // invokes StateMachine.OnTransitionedAsync's callback (BaseBehavior.HandleTransitioned,
        // which raises Transitioned and confirms it) BEFORE the destination state's
        // OnEntryFromAsync action - i.e. THIS method runs after that confirm has already
        // happened, not before. Each CreateChild call below raises a ChildCreated event on THIS
        // Stage/CasePlanModel instance (not the child - the child confirms its own Defined/
        // Transitioned independently), and nothing later in the current transition confirms
        // them, so without an explicit confirm here they remain queued in TentativeState
        // indefinitely. Confirmed once after all children are created rather than per-child:
        // ConfirmEvents() persists everything queued so far, so one call covers the batch.
        protected async Task HandleEnterActiveFromStart()
        {
            await Task.WhenAll(PlanItemDefinition.PlanItems
                .Select(x => CreateChild(x)));

            await Host.ConfirmEvents();

            // #180 - Table 8.12 completion is otherwise evaluated EXCLUSIVELY from
            // HandleChildTransitioned, gated on an actual child PlanItemTransitionedEvent
            // arriving. A Stage with zero PlanItems makes the Task.WhenAll fan-out above a no-op
            // over an empty sequence, so no child transition ever happens and that evaluation
            // would never run - an autoComplete=TRUE empty Stage (which satisfies "no Active
            // children, all required children terminal" VACUOUSLY, over the empty set) would sit
            // Active forever. Evaluate once here instead, for exactly that shape.
            //
            // PlanningTable is irrelevant to this gate, deliberately: Table 8.12's
            // autoComplete=TRUE column carries no "no DiscretionaryItems pending" term at all -
            // that conjunct exists ONLY in the autoComplete=FALSE column's Branch 1
            // (HandleChildTransitioned's noDiscretionaryItemsPending, below). A Stage with zero
            // PlanItems and a non-null PlanningTable (every child it could ever have is
            // discretionary) completes here exactly as vacuously as one with no PlanningTable at
            // all - do NOT add a PlanningTable check to this condition; doing so would carve out
            // an autoComplete=TRUE exception Table 8.12 never states, and would reintroduce a
            // second, divergent definition of "complete" at exactly the seam TryAutoComplete
            // exists to prevent. See Conformance/LifecycleScenarios.cs's
            // StageCompletion__Given_AutoCompleteStageWithOnlyDiscretionaryItems__… for the
            // scenario pinning this reading.
            //
            // Ordering (why this is safe): this call sits AFTER the fan-out's Task.WhenAll and
            // AFTER Host.ConfirmEvents(), i.e. only once every child this activation will ever
            // synchronously create has actually been created (CreateChild's ChildCreated event
            // is applied to StageStore.Children synchronously via RaiseEvent, well before this
            // line runs) - never before, so a Stage that legitimately has children can never be
            // observed here as if it had none.
            //
            // Gated on !PlanItemDefinition.PlanItems.Any() (not just "re-run the same check
            // Branch handles"): this is the one piece of information HandleChildTransitioned
            // can never have, and it is what makes this call safe against the sharpest adjacent
            // hazard - a repeating child. A PlanItem with a RepetitionRule is NOT absent from
            // PlanItemDefinition.PlanItems; its repetition-0 instance IS created by the fan-out
            // above like any other child, so a Stage with such a PlanItem always has
            // PlanItems.Any() == true and this block never runs for it at all. Whether that
            // repetition-0 child happens to reach a terminal state synchronously within the
            // fan-out (and whether its own next-repetition spawn - published asynchronously via
            // PlanItemRepetitionCriteriaMetEvent, see BaseBehavior.TryRepeatOnCompleteOrTerminate
            // - has been observed by this Stage yet) is completely irrelevant here, because this
            // branch is unreachable whenever PlanItems is non-empty. That pre-existing
            // interaction between HandleChildTransitioned's own reactive evaluation and a
            // not-yet-delivered repetition event is unchanged by this fix either way.
            //
            // Gated on PlanItemDefinition.AutoComplete too: autoComplete=FALSE requires EXPLICIT
            // completion (Table 8.12's Manual Completion branch, enforced by
            // ManualCompletionCriteriaSatisfied/Trigger's override below) - an empty
            // autoComplete=FALSE Stage must NOT auto-complete just because it happens to have no
            // children, so this call is skipped entirely for that combination and the Stage
            // waits, exactly as it does today, for an external Trigger(Complete).
            //
            // Reuses TryAutoComplete - the exact predicate-and-act HandleChildTransitioned's own
            // AutoComplete branch calls - so the two call sites can never drift onto different
            // definitions of "complete".
            if (PlanItemDefinition.AutoComplete && !PlanItemDefinition.PlanItems.Any())
            {
                await TryAutoComplete(await GetChildInstances());
            }
        }

        protected override async Task HandleSentrySatisfied(SentrySatisfiedEvent @event, StreamSequenceToken token = null)
        {
            // 5.4.5.1 - Criterion, Table 5.25 - Criterion attributes
            // ~~~~~
            // Reference a Sentry that represents the PlanItem’s entry or exit criteria.
            // Criteria of a PlanItem MUST refer to Sentries that are contained by the
            // Stage or PlanFragment that contains that PlanItem.
            if (Host.Scope != @event.SourceScope) return;

            var criterion = Host.Definition
                .EntryCriteria.Union<Criterion>(Host.Definition.ExitCriteria)
                .SingleOrDefault(x => x.SentryRef == @event.SourceDefinitionId);

            // disregard events in our scope, but are not our concern
            if (criterion == null) return;

            Host.LogWithContext(logger => logger.LogInformation(
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | {CriterionType} {CriterionId} satisfied by sentry {SentryRef}.  OnPart: {OnPartOccurred}",
                Host.Definition.GetType().Name,
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                criterion.GetType().Name,
                criterion.Id,
                @event.SourceDefinitionId,
                @event.OnPartOccurred));

            if (criterion is EntryCriterion)
            {
                // ADO #183 - raise the event matching the criterion's actual type. Previously
                // EntryCriterionSatisfied was raised unconditionally before this branch, so an
                // ExitCriterion satisfaction also journaled a spurious EntryCriterionSatisfied
                // and corrupted PlanItemStore.EntryCriterionStore for PlanItems with no entry
                // criterion at all (PlanItemStore.Apply(EntryCriterionSatisfied) applies to
                // EntryCriterionStore regardless of source). See the ExitCriterion branch below
                // for the mirrored fix on the exit side.
                Host.RaiseEvent(new EntryCriterionSatisfied
                {
                    SourceScope = @event.SourceScope,
                    SourceId = @event.SourceDefinitionId,
                    OnPartOccurred = @event.OnPartOccurred
                });

                // disregard entry criteria if already repeated
                //   - just being defensive. this should never happen as entry criteria
                //     subs are removed on repeat
                if (Host.State.Repeated) return;

                if (@event.OnPartOccurred && Host.State.PlanItemState == PlanItemState.Uninitialized)
                {
                    await StateMachine.FireAsync(PlanItemTransition.Create);
                }

                if (Host.State.PlanItemState == PlanItemState.Available)
                {
                    // 8.6.2 ManualActivationRule
                    // ~~~~~
                    // If this rule evaluates to TRUE, the Task or Stage instance transitions from Available
                    // to Enabled (via Enable), otherwise it transitions from Available to Active (via Start).
                    if (await EvaluateManualActivationRule())
                    {
                        await StateMachine.FireAsync(PlanItemTransition.Enable);
                    }
                    else
                    {
                        await StateMachine.FireAsync(PlanItemTransition.Start);
                    }
                }
                // 8.6.4 RepetitionRule
                // ~~~~~
                // (after transitioning from Available), every time an entry criterion with an OnPart is satisfied
                // the RepetitionRule’s condition is re-evaluated and if it evaluates to TRUE, a new instance of the
                // Task, Stage, or Milestone is created and transition to Available
                else if (@event.OnPartOccurred && await EvaluateRepetitionRule())
                {
                    await Task.WhenAll(
                        UnsubscribeFromCriteria(x => x.EntryCriteria),
                        Host.Publish(new PlanItemRepetitionCriteriaMetEvent(
                            Host.Scope,
                            Host.InstanceId,
                            Host.DefinitionId,
                            Host.State.Repetition)));

                    Host.RaiseEvent(new Repeated());
                }
            }
            else if (criterion is ExitCriterion)
            {
                // ADO #186 - journal the satisfaction unconditionally, mirroring the EntryCriterion
                // branch above: the sentry genuinely fired regardless of whether the resulting
                // transition can be taken right now. Previously this raise sat INSIDE the
                // CanFire(ExitCriterionTransition) guard, so a genuine satisfaction that arrived
                // while the transition could not fire journaled nothing at all - invisible to any
                // future replay (JournaledGrain is a pure fold). Concretely reachable via the
                // CasePlanModel (ExitCriterionTransition overridden to Terminate below): Table 8.6
                // permits no `terminate` edge out of Suspended or Failed, yet the ExitCriteria
                // subscription (armed once, on entry to Active from Create - see
                // CasePlanModelBehavior.HandleEnterActiveFromCreate) stays live through both, and
                // SentryGrain publishes SentrySatisfiedEvent purely from OnPart/IfPart evaluation
                // with no check at all on the referencing PlanItem's own state. Only the TRANSITION
                // is conditional on CanFire; the journaled FACT that the criterion was satisfied is
                // not - same discipline as #183's mirror-image fix on the entry side.
                Host.RaiseEvent(new ExitCriterionSatisfied
                {
                    SourceScope = @event.SourceScope,
                    SourceId = @event.SourceDefinitionId,
                    OnPartOccurred = @event.OnPartOccurred
                });

                if (StateMachine.CanFire(ExitCriterionTransition))
                {
                    // D10 - only the Exit trigger carries a parameterized ExitCriterionRef (see
                    // PlanItemStateMachine.FireAsync(PlanItemTransition, string) / BaseBehavior.
                    // HandleTransitioned). ExitCriterionTransition is Terminate for the CasePlanModel
                    // (CasePlanModelBehavior's override - Table 8.6 has no `exit` row for it), which
                    // has no such registered trigger parameter, so that path keeps firing plain.
                    if (ExitCriterionTransition == PlanItemTransition.Exit)
                    {
                        await StateMachine.FireAsync(PlanItemTransition.Exit, criterion.Id);
                    }
                    else
                    {
                        await StateMachine.FireAsync(ExitCriterionTransition);
                    }
                }
            }

            // ADO #186 - the RaiseEvent calls above have no guaranteed confirm to ride on. On the
            // FireAsync paths, BaseBehavior.HandleTransitioned already confirms (it raises
            // Transitioned and awaits Host.ConfirmEvents() before this method's own await resumes),
            // so this is a harmless no-op there - Host.State => TentativeState reads back a queue
            // that is already empty. But whenever this method returns WITHOUT ever calling
            // FireAsync - the exact CanFire(ExitCriterionTransition) == false case #186 exists to
            // journal, and identically the EntryCriterion branch reaching neither the
            // Available-state Enable/Start arm nor a successful repetition re-evaluation - nothing
            // upstream confirms at all. The event then sits unconfirmed in TentativeState (#160)
            // until whatever next activity happens to confirm something else, and is lost outright
            // if this grain deactivates idle before that happens. MilestoneBehavior.
            // HandleSentrySatisfied already ends on exactly this same unconditional trailing
            // confirm, for the same reason (see BaseBehavior's remarks on Bug #61 discipline) -
            // this brings StageBehavior in line with that precedent rather than introducing a new
            // pattern.
            await Host.ConfirmEvents();
        }

        protected override async Task HandleParentTransitioned(PlanItemTransitionedEvent @event, StreamSequenceToken token = null)
        {
            // ignore if not from direct parent
            if (@event.SourceInstanceId != Host.ParentInstanceId) return;

            PlanItemTransition? transition = null;
            switch (@event.StandardEvent)
            {
                case PlanItemTransition.Suspend:
                case PlanItemTransition.ParentSuspend:
                    {
                        Host.RaiseEvent(new ParentSuspended());
                        transition = PlanItemTransition.ParentSuspend;
                        break;
                    }
                case PlanItemTransition.Resume:
                case PlanItemTransition.ParentResume:
                // Table 8.6 re-activate + Table 8.9 note (2) (#63 D8 carve-out): the Case leaves
                // Suspended via Reactivate, not Resume (ConfigureForCasePlanModel permits
                // Reactivate, not Resume/ParentResume, from Suspended) - so a cascaded-suspended
                // direct child of the CasePlanModel would otherwise never see a transition its
                // switch recognizes. Folded into the same arm as Resume/ParentResume: the
                // ParentResume state-machine permit is already guarded by
                // ParentSuspendState.HasValue (PlanItemStateMachine.ConfigureForStageOrTask), so
                // StateMachine.CanFire below naturally no-ops for a child that wasn't cascaded
                // into Suspended (e.g. the case reactivating from Completed/Terminated/Failed).
                case PlanItemTransition.Reactivate:
                    {
                        Host.RaiseEvent(new ParentResumed());
                        transition = PlanItemTransition.ParentResume;
                        break;
                    }
                case PlanItemTransition.Exit:
                case PlanItemTransition.Terminate:
                    {
                        Host.RaiseEvent(new ParentTerminated());
                        transition = PlanItemTransition.Exit;
                        break;
                    }
                // #179 - Table 8.9's `complete` rows: a Completed Stage may coexist ONLY with
                // children in {Disabled, Completed, Terminated, Failed} - Available, Enabled,
                // Active, and Suspended are explicitly marked impossible. Table 8.12's
                // autoComplete=TRUE completion criteria ("no Active children AND all REQUIRED
                // children terminal") says nothing about non-required children, so a stage can
                // legitimately reach Completed while a non-required child still sits in Available/
                // Enabled - the two tables only reconcile if completion itself drives that
                // remainder to a terminal state, the same way Terminate/Exit above already
                // quiesces the whole subtree. So: cascade Exit here too, but - unlike the Exit/
                // Terminate case above, which deliberately targets every non-terminal state - gate
                // it on Host.State.PlanItemState not already being terminal. IsTerminal() is
                // exactly Table 8.9's "may coexist" set (Disabled, Completed, Terminated, Failed),
                // so this reuses that predicate rather than re-deriving the same four states here:
                // a Disabled or already-Failed child is left alone, matching the table precisely,
                // where the bare CanFire(Exit) gate the Exit/Terminate case relies on would not -
                // ConfigureForStageOrTask permits Exit from Disabled and Failed too (a genuine
                // termination cascade DOES reach into those), so Complete needs its own, narrower
                // condition rather than reusing that gate unchanged.
                //
                // Type asymmetry, deliberate: Table 8.9's `complete` rows carry SEPARATE columns
                // per child type - Stage and Task instances in {Available, Enabled, Active,
                // Suspended} are `<impossible>` (this cascade), but Milestone and EventListener
                // instances in Available/Suspended are explicitly permitted to REMAIN Available/
                // Suspended under a Completed parent (they legitimately survive it). Table 8.7's
                // own description of a completed Stage confirms this: it names only "Stage or Task
                // instances" as needing to be Completed/Terminated, conspicuously omitting
                // Milestone/EventListener. TaskBehavior.HandleParentTransitioned carries the
                // identical case (ConfigureForStageOrTask is shared, so IsTerminal()+Exit port
                // verbatim); MilestoneBehavior/EventListenerBehavior deliberately do NOT - adding
                // it there would itself be a Table 8.9 violation, just in the opposite direction.
                case PlanItemTransition.Complete:
                    {
                        if (!Host.State.PlanItemState.IsTerminal())
                        {
                            Host.RaiseEvent(new ParentCompleted());
                            transition = PlanItemTransition.Exit;
                        }
                        break;
                    }
            }

            if (transition.HasValue && StateMachine.CanFire(transition.Value))
            {
                await StateMachine.FireAsync(transition.Value);
            }
        }

        private async Task HandleChildTransitioned(PlanItemTransitionedEvent @event, StreamSequenceToken token = null)
        {
            // ignore events that are not direct children
            if (@event.SourceScope != Host.Address) return;

            Host.LogWithContext(logger => logger.LogInformation(
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | processing instance {ChildElementInstanceId} of child {ChildElementDefinitionId} transition {PreviousState} × {StandardEvent} = {CurrentState}",
                Host.Definition.GetType().Name,
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                @event.SourceInstanceId,
                @event.SourceDefinitionId,
                @event.Source,
                @event.StandardEvent,
                @event.Destination));

            // 8.7 - Stage and Task instances states
            // ~~~~~
            // The (child) Stage or Task has a Sentry with an OnPart that has a sourceRef outside of the enclosing
            // Stage of that Stage or Task and this Sentry is satisfied. In that case, the Stage instance and
            // recursively all Stage instances up to the enclosing Stage of the Stage or Task in which the
            // Stage or Task resides moves into Active state if not already active.
            //
            // Note: we are interpreting this as overriding any ManualActivationRule
            if (Host.State.PlanItemState < PlanItemState.Active && @event.Destination == PlanItemState.Available)
            {
                Host.RaiseEvent(new ChildTriggeredActivation());
                if (Host.State.PlanItemState == PlanItemState.Uninitialized)
                {
                    await StateMachine.FireAsync(PlanItemTransition.Create);
                }
                if (Host.State.PlanItemState == PlanItemState.Available)
                {
                    await StateMachine.FireAsync(PlanItemTransition.Start);
                }
                if (Host.State.PlanItemState == PlanItemState.Disabled)
                {
                    await StateMachine.FireAsync(PlanItemTransition.Reenable);
                }
                if (Host.State.PlanItemState == PlanItemState.Enabled)
                {
                    await StateMachine.FireAsync(PlanItemTransition.ManualStart);
                }
            }

            // Table 8.12's automatic criteria (and the UserCompletable latch that shadows its
            // manual branch) can only change their answer when a child reaches one of Table 8.12's
            // OWN states - {Disabled, Completed, Terminated, Failed}, exactly PlanItemState.
            // IsTerminal()'s set - so a non-terminal destination has nothing to evaluate. Every
            // other route into TryCompleteStage is a repetition request being resolved
            // (TryCompleteStageAfterRepetitionResolved), which changes the gate's answer without
            // any child transitioning at all.
            if (@event.Destination.IsTerminal())
            {
                // #198 - fetched once as (instance id, snapshot) pairs: the blocking predicate
                // needs the child's INSTANCE id (its key into this Stage's own settled-request
                // record), which a bare PlanItemSnapshot does not carry. See GetChildInstances.
                await TryCompleteStage(await GetChildInstances());
            }
        }

        // Table 8.12 - Stage instance termination criteria
        // ~~~~~
        // autoComplete = TRUE
        // There are no Active children, AND all required (requiredRule evaluates to TRUE) children are
        // in {Disabled, Completed, Terminated, Failed}.
        //
        // autoComplete = FALSE
        // (There are no Active children AND all children are in {Disabled, Completed, Terminated, Failed}
        // AND there are no DiscretionaryItems) OR (Manual Completion AND all required (requiredRule evaluates
        // to TRUE) children are in { Disabled, Completed, Terminated, Failed}).
        //
        // These are two INDEPENDENT OR-branches, not one combined condition: Branch 1 (below)
        // is the automatic path; Branch 2 ("Manual Completion") is NOT evaluated here at all - it
        // is an explicitly-invoked action (an external Trigger(Complete) call), gated by Trigger's
        // override below, and deliberately does not require non-required children to also be done.
        //
        // THE one place the automatic criteria are evaluated (#198 F1). It exists because the
        // criteria now have two genuinely different triggers, and a second, divergent notion of
        // "complete" at the seam between them is precisely how #198 happened in the first place:
        //   - a child reaching a Table 8.12 state (HandleChildTransitioned above), and
        //   - a repetition request being resolved (TryCompleteStageAfterRepetitionResolved below),
        //     which needs no child transition at all and, in the shape that made this necessary,
        //     never produces one: a spawned successor whose ManualActivationRule is TRUE lands
        //     Enabled and simply sits there.
        // TryAutoComplete stays factored out below because #180 calls it from a THIRD place
        // (HandleEnterActiveFromStart's zero-PlanItems case) that must not evaluate the
        // autoComplete=FALSE arm or the UserCompletable latch.
        private async Task TryCompleteStage((string InstanceId, PlanItemSnapshot Snapshot)[] children)
        {
            var childSnapshots = children.Select(c => c.Snapshot).ToArray();

            // Read twice below - by the UserCompletable latch and by Branch 1 - so evaluated once
            // here. (TryAutoComplete evaluates its own; the predicate is pure and does no I/O, see
            // RepetitionRequestsAwaitingResolution, so that costs nothing and keeps that method
            // callable on its own from HandleEnterActiveFromStart.) Only the Warning is deferred,
            // to the point where a completion was genuinely held.
            var blocking = RepetitionRequestsAwaitingResolution(children);

            // Table 8.12 - UserCompletable (#68, D4 residual): the UI-facing observability twin
            // of ManualCompletionCriteriaSatisfied's autoComplete=FALSE arm below, not of the
            // autoComplete=TRUE column. The two columns are genuinely different completion
            // branches: autoComplete=TRUE has NO Manual Completion branch in Table 8.12 at all -
            // it auto-completes on its own with no human involvement (the AutoComplete branch
            // further down) - while autoComplete=FALSE's Manual Completion OR-branch requires
            // only that REQUIRED children be terminal, carrying no "no Active children" conjunct
            // (a still-Active non-required child must not block it - the original D4 finding).
            // The prior condition here conflated both: it ignored AutoComplete entirely (so an
            // autoComplete=TRUE stage got latched "user-completable" even though a human cannot
            // legitimately manually-complete it in that mode) and required ALL children
            // non-Active - not just required ones - before flipping the flag (so an
            // autoComplete=FALSE stage with a lingering non-required Active child never latched,
            // even after !26/#19 fixed the actual Trigger(Complete) enforcement gate to permit
            // exactly that). Gating on !PlanItemDefinition.AutoComplete and dropping the
            // no-Active-children conjunct keeps this flag true only where the spec's Manual
            // Completion branch actually is.
            //
            // #198 F3 - and gated on the repetition gate too, silently (no Warning: this is not a
            // completion attempt, so logging a "holding completion" line here would be false).
            // Trigger below refuses a manual Complete while a repetition is owed, so latching this
            // flag without consulting the same predicate advertises to the UI a Complete that would
            // throw. The latch stays MONOTONIC either way - nothing ever clears it - so this only
            // delays it; the delay ends the moment the request resolves, because
            // TryCompleteStageAfterRepetitionResolved routes back through this same method. In the
            // residual lost-message window (see Trigger's remarks) it never latches at all, which
            // is exactly right: a Complete really is unavailable there.
            if (!PlanItemDefinition.AutoComplete &&
                !Host.State.UserCompletable &&
                StateMachine.CanFire(PlanItemTransition.Complete) &&
                childSnapshots.Where(x => x.Required).All(x => x.PlanItemState.IsTerminal()) &&
                !blocking.Any())
            {
                Host.RaiseEvent(new UserCompletableCriteriaMet());
            }

            if (PlanItemDefinition.AutoComplete)
            {
                await TryAutoComplete(children);
                return;
            }

            // Branch 1: ...There are no Active children AND all children (not just required
            // ones) are in {Disabled, Completed, Terminated, Failed} AND there are no
            // DiscretionaryItems left that a Case worker could still plan. "No
            // DiscretionaryItems" is a standalone structural condition - it must not depend on
            // Host.State.UserCompletable (a latched UI hint related to Branch 2, a different
            // OR-branch entirely); conflating the two here was the original bug: a stage whose
            // PlanningTable's items were all already planned could only complete if the
            // UserCompletable latch happened to be set, and (because .All() on an empty
            // sequence is true) a PlanningTable with zero DiscretionaryItems demanded the same
            // latch for no reason. NOTE the null-check IS still doing double duty as "no
            // DiscretionaryItems": an absent PlanningTable and a present-but-fully-planned one
            // are both correctly "none pending" - .All() covers the empty table too.
            var noDiscretionaryItemsPending =
                PlanItemDefinition.PlanningTable == null ||
                PlanItemDefinition.PlanningTable.DiscretionaryItems
                    .All(di => childSnapshots.Any(snap => snap.Definition.Id == di.Id));

            if (childSnapshots.All(x => x.PlanItemState.IsTerminal()) &&
                noDiscretionaryItemsPending &&
                StateMachine.CanFire(PlanItemTransition.Complete))
            {
                // #198 - the repetition gate is checked LAST deliberately: it logs at Warning
                // whenever it holds up a completion, and reaching it only once every other Table
                // 8.12 term is already satisfied keeps that log to genuine "this Stage would have
                // completed right now" events rather than one line per child transition of a Stage
                // that was never close to completing.
                if (blocking.Any())
                {
                    LogCompletionHeldForRepetition(blocking, "automatic (Table 8.12 autoComplete=FALSE, Branch 1)");
                    return;
                }

                Host.RaiseEvent(new FullyCompleteCriteriaMet());
                await StateMachine.FireAsync(PlanItemTransition.Complete);
            }
        }

        // #198 F1 - the release half of the gate. RepetitionRequestsAwaitingResolution holds Table
        // 8.12 completion while a request is outstanding; something has to re-open that decision
        // once the request is resolved, and HandleChildTransitioned cannot: it only runs on a child
        // transition, and resolving a request produces none that Table 8.12 reacts to. A successor
        // spawned with a TRUE ManualActivationRule (8.6.2/Table 5.51's absence default) lands
        // Enabled - neither Active nor terminal - so the container would satisfy Table 8.12 in full
        // and sit Active forever, its one chance to notice already spent on the check the gate
        // refused. Called from every path that resolves a request: HandleChildRepeated (all
        // branches except #178's Suspended buffer, where the request is genuinely still pending)
        // and the tail of DrainPendingRepetitions.
        //
        // Routes through TryCompleteStage rather than restating anything: there is exactly one
        // definition of Table 8.12's automatic criteria and this is not a second one.
        //
        // REENTRANCY - both call sites are accounted for, and they are NOT the same:
        //  - from HandleChildRepeated this runs in a stream-delivery turn, so the FireAsync(Complete)
        //    inside TryCompleteStage runs to completion and BaseBehavior.HandleTransitioned's own
        //    ConfirmEvents commits everything raised on the way.
        //  - from DrainPendingRepetitions this runs inside an OnEntryFromAsync action of the
        //    Resume/ParentResume/Reactivate transition still being dispatched, so that FireAsync is
        //    REENTRANT: Stateless (FiringMode.Queued) only ENQUEUES it and drains the queue once the
        //    outermost FireAsync unwinds - see DrainPendingRepetitions' own ceilingBreached remarks
        //    for the same fact. That is fine and needs no ordering fix here (the queued Complete
        //    still runs, still within this activation, and its HandleTransitioned still confirms),
        //    but it is why the guard below reads Host.State.PlanItemState BEFORE the fire and never
        //    after it: on that path Host.State does not advance within the turn.
        private async Task TryCompleteStageAfterRepetitionResolved()
        {
            // Only an Active container can complete (Table 8.8: complete is Active->Completed).
            // This is also what makes it safe to call this unconditionally from HandleChildRepeated:
            // the terminal-container, Failed-container and unexpected-state branches all settle the
            // request and then land here, and all of them are correctly no-ops.
            if (Host.State.PlanItemState != PlanItemState.Active) return;

            var children = await GetChildInstances();

            // A container with no child instances at all cannot have produced the request that got
            // us here, so this is an unreachable-in-production shape rather than a completion to
            // evaluate. Guarded explicitly so Table 8.12's VACUOUS reading for a childless Stage
            // stays where #180 deliberately put it - HandleEnterActiveFromStart, autoComplete=TRUE
            // only - instead of leaking an autoComplete=FALSE vacuous auto-completion in through
            // this back door (see Conformance/LifecycleScenarios.cs's
            // StageCompletion__Given_NotAutoCompleteStageWithZeroPlanItems__… for the reading that
            // would break).
            if (!children.Any()) return;

            await TryCompleteStage(children);
        }

        // Table 8.12 - Stage instance termination criteria, autoComplete = TRUE column:
        // "There are no Active children, AND all required (requiredRule evaluates to TRUE)
        // children are in {Disabled, Completed, Terminated, Failed}." Factored out of
        // HandleChildTransitioned (#180) so it has exactly one definition, called from two
        // places that must never be allowed to drift onto different notions of "complete":
        //   - TryCompleteStage above, i.e. every reactive evaluation - a terminal child
        //     transition, or a repetition request being resolved (#198 F1);
        //   - HandleEnterActiveFromStart, once, for the zero-PlanItems case that would
        //     otherwise never produce a child transition to react to at all (see that method's
        //     remarks for why it is safe to call this from there).
        // Takes the already-fetched snapshots rather than re-fetching: TryCompleteStage already
        // has a live set for its own UserCompletable check; HandleEnterActiveFromStart fetches its
        // own (necessarily empty, in the only shape it calls this for).
        //
        // Named Try*, not Evaluate* (unlike EvaluateManualActivationRule/EvaluateRequiredRule/
        // EvaluateRepetitionRule, this file's existing Evaluate* family - all side-effect-free
        // Task<bool> predicates): this method both evaluates the predicate AND fires the
        // transition when it holds, mirroring the file's own evaluate-and-act precedent,
        // BaseBehavior.TryRepeatOnCompleteOrTerminate. Calling it "Evaluate*" would read as a pure
        // query and risk a future caller assuming it is safe to call speculatively.
        private async Task TryAutoComplete((string InstanceId, PlanItemSnapshot Snapshot)[] children)
        {
            // ...There are no Active children
            if (children.All(x => x.Snapshot.PlanItemState != PlanItemState.Active) &&
                // ... all required children are in {Disabled, Completed, Terminated, Failed}
                children.Where(x => x.Snapshot.Required).All(x => x.Snapshot.PlanItemState.IsTerminal()) &&
                StateMachine.CanFire(PlanItemTransition.Complete))
            {
                // #198 - checked last deliberately; see the Branch-1 call site's remarks for why
                // (the hold is logged at Warning, and only a completion actually held up is worth
                // a line).
                var blocking = RepetitionRequestsAwaitingResolution(children);
                if (blocking.Any())
                {
                    LogCompletionHeldForRepetition(blocking, "automatic (Table 8.12 autoComplete=TRUE)");
                    return;
                }

                Host.RaiseEvent(new AutoCompleteCriteriaMet());
                await StateMachine.FireAsync(PlanItemTransition.Complete);
            }
        }

        // #198 - the single definition of "a child has declared a repetition this container has
        // not yet dealt with", evaluated on EVERY Table 8.12 completion path (TryAutoComplete
        // above, TryCompleteStage's autoComplete=FALSE Branch 1 and its UserCompletable latch, and
        // Trigger's manual branch below - which covers CasePlanModelBehavior too, since that class
        // overrides only ManualCompletionCriteriaSatisfied and inherits this Trigger unchanged).
        // Returns the blocking child INSTANCE ids, empty when nothing blocks.
        //
        // Pure and side-effect-free (no I/O, no logging, no events) - the Warning that makes a held
        // completion diagnosable is LogCompletionHeldForRepetition below, raised by the callers
        // that are genuinely refusing a completion, so the latch check can consult the same
        // predicate without claiming a completion was held.
        //
        // 8.6.4 + Table 8.9: a child that has durably raised Repeated has determined a repetition -
        // a successor instance that MUST exist. Completing this Stage over it drops that instance
        // outright, and, once the request finally arrives, #178 correctly refuses to spawn into a
        // now-terminal container - i.e. the repetition is lost silently, and (before #178) the
        // spawn produced Table 8.9's <impossible> Completed-parent/Available-child cell. Blocking
        // completion until the request is resolved is the only reading of Table 8.12 that keeps
        // 8.6.4 and Table 8.9 simultaneously true.
        //
        // Three conjuncts:
        //  1. IsTerminal() - so this predicate only ever adds a constraint for a child Table 8.12
        //     would otherwise consider DONE; a still-live child is already handled by Table 8.12's
        //     own terms and needs nothing from here. Note both repetition paths can land in this
        //     conjunct's scope, which is why it is stated as a property of the CHILD rather than of
        //     the path: the no-entry-criteria path (BaseBehavior.TryRepeatOnCompleteOrTerminate)
        //     always raises Repeated on a child entering Completed/Terminated, and the
        //     entry-criterion path raises it on a child that is still live for a Stage/Task
        //     (StageBehavior/TaskBehavior.HandleSentrySatisfied, from Available/Active) but on an
        //     already-Completed one for a Milestone (MilestoneBehavior.HandleSentrySatisfied's
        //     IsTerminal() branch - a milestone repeats only AFTER it has occurred). A Milestone
        //     therefore does block its container here, deliberately: 8.6.4 grants Milestones
        //     repetition too, and a determined-but-unspawned milestone repetition is lost exactly
        //     the same way.
        //  2. Snapshot.Repeated - read from the child's OWN live, confirmed state via a direct
        //     grain call (GetChildInstances), never from a stream. This is what makes the fix
        //     race-free: PlanItemGrain is not [Reentrant], so a child's Complete/Terminate turn -
        //     which raises and CONFIRMS Repeated in BaseBehavior.TryRepeatOnCompleteOrTerminate's
        //     entry action before the turn ends - cannot be interleaved by this container's
        //     GetSnapshot call. A child observable as terminal is therefore always already
        //     observable as Repeated if it is going to repeat at all. Measured on this branch
        //     before the fix was written: across 10 runs of the #198 shape, 42 parent-side reads,
        //     a terminal child never once read back Repeated=false.
        //  3. !IsRepetitionSettled - this container's own durable, monotonic record of having
        //     spawned (ChildRepeated, #161) or definitively refused (RepetitionRequestSettled) the
        //     request. Parent-local, so it cannot race the signal in (2).
        //
        // WHY (3) IS A JOURNALED RECORD RATHER THAN SOMETHING DERIVED from state already on hand.
        // It is tempting to read it off StageStore.Children instead ("has a later repetition of the
        // same PlanItem been created?"), and most of the refusing branches would indeed survive
        // that: the Completed/Terminated/Closed refusal is unreachable by this predicate at all
        // (those three states have no outgoing transition back to a Table 8.12 evaluation, so
        // nothing would ever ask), and the #67 ceiling refusal is expressible as a
        // `Snapshot.Repetition + 1 < ceiling` conjunct. Two things are NOT derivable, and they are
        // the whole reason this set exists:
        //  a. A FAILED container that is later Reactivated. The Failed branch of HandleChildRepeated
        //     refuses without spawning, and neither this class (no Reactivate hook at all) nor
        //     CasePlanModelBehavior (its hook is guarded to transition.Source == Suspended) replays
        //     the request afterwards - both deliberately, see their constructors. So the container
        //     comes back Active with a terminal, Repeated child and nothing anywhere in derivable
        //     state saying "that one was already answered": it would block Complete forever.
        //  b. Configuration is not durable, and this predicate must be. Deriving the ceiling case
        //     would make a completion decision depend on the CURRENT value of
        //     RepetitionGuardOptions.MaxRepetitionsPerPlanItem, so raising that limit between
        //     deployments would resurrect a block on a request this container already refused under
        //     the old limit - a case that completed fine yesterday stops completing after a config
        //     change. A journaled record cannot do that: it says what this container DID, not what
        //     its configuration currently implies.
        // (The unrecognized-child branches settle too, but on their own they are a weak argument -
        // they are defensive paths that should not be reachable in production.)
        //
        // Deliberately NOT gated on Required: losing a determined repetition is a defect whether
        // or not the repeating item's requiredRule is TRUE. Table 8.12's Required term scopes which
        // children must be FINISHED; it says nothing about a child that is finished but has already
        // spawned an obligation the container has yet to honour.
        private string[] RepetitionRequestsAwaitingResolution((string InstanceId, PlanItemSnapshot Snapshot)[] children) =>
            children
                .Where(x => x.Snapshot.PlanItemState.IsTerminal() &&
                            x.Snapshot.Repeated &&
                            !StageStore.IsRepetitionSettled(x.InstanceId))
                .Select(x => x.InstanceId)
                .ToArray();

        // #198 - the diagnosability half of the escape hatch (see Trigger's remarks): a Stage that
        // stops completing must be explainable from logs alone, naming exactly which child
        // instances are responsible. Call ONLY from a path that is actually refusing a completion,
        // and only once every other Table 8.12 term is already satisfied - otherwise this degrades
        // into one line per child transition of a Stage that was never close to completing, and
        // stops being a signal at all.
        private void LogCompletionHeldForRepetition(string[] blocking, string completionPath) =>
            Host.LogWithContext(logger => logger.LogWarning(
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | holding {CompletionPath} completion - child instance(s) {BlockingInstanceIds} declared a repetition (8.6.4) this container has neither spawned nor refused; completing now would drop it (#198)",
                Host.Definition.GetType().Name,
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                completionPath,
                string.Join(", ", blocking)));

        // Table 8.12 - Stage instance termination criteria, evaluated LIVE at the moment of an
        // externally-invoked (manual) Complete - a Case worker completing a Stage by hand is
        // exactly a Trigger(Complete) call arriving from outside (via IPlanItemGrain.Trigger /
        // ICaseGrain.Trigger), as opposed to Branch 1 above, which HandleChildTransitioned
        // evaluates and fires automatically on its own.
        //
        // autoComplete=FALSE, Branch 2 ("Manual Completion AND all required (requiredRule
        // evaluates to TRUE) children are in {Disabled, Completed, Terminated, Failed}"): read the
        // OR-structure precisely - unlike Branch 1 and unlike the autoComplete=TRUE column, this
        // branch does NOT carry a "there are no Active children" conjunct and does NOT care about
        // pending DiscretionaryItems. A still-Active NON-required child therefore must not block
        // manual completion - which is the D4 headline: previously no manual-completion gate
        // existed at all, so an external Complete fell through to the bare Stateless permit with
        // no Table 8.12 check whatsoever, while the only criteria evaluation that DID exist (the
        // automatic one above) required ALL children terminal.
        //
        // autoComplete=TRUE has no manual branch in Table 8.12 - its single column IS the
        // completion criteria for any complete transition (8.8/Table 8.8: "For a Stage instance,
        // the termination criteria described in Table 8.12 ... must be satisfied"), so a manual
        // Complete on an autoComplete stage is held to that same column: no Active children AND
        // required children terminal.
        //
        // Evaluated live from child snapshots rather than from the latched
        // Host.State.UserCompletable flag: the latch is monotonic (never reset), so it both
        // understates (a stage whose only blocker was a non-required Active child never latched
        // under the old combined condition) and can overstate (a repetition spawning a NEW
        // required child after the latch would leave it stale-true). The latched event remains as
        // a UI hint; enforcement reads current state.
        //
        // #198 - takes the caller's already-fetched snapshots and is synchronous now: Trigger
        // below needs the SAME live read for its own repetition gate, and re-fetching every
        // child's snapshot twice per manual Complete (once here, once there) would be pure waste
        // and would let the two gates judge two different populations. Repetition blocking is NOT
        // folded into this method: this one is Table 8.12's own criteria (overridden wholesale by
        // CasePlanModelBehavior for the Case lifecycle's different completion rule, Table 8.5/8.6),
        // whereas the repetition gate applies identically to both and must not be duplicated into
        // - or accidentally dropped by - an override.
        protected virtual bool ManualCompletionCriteriaSatisfied(PlanItemSnapshot[] childSnapshots)
        {
            var requiredChildrenTerminal = childSnapshots
                .Where(x => x.Required)
                .All(x => x.PlanItemState.IsTerminal());

            return PlanItemDefinition.AutoComplete
                ? childSnapshots.All(x => x.PlanItemState != PlanItemState.Active) && requiredChildrenTerminal
                : requiredChildrenTerminal;
        }

        // Gate ONLY an externally-invoked Complete, and only when the state machine could
        // otherwise fire it - a Complete attempted from a non-Active state still falls through to
        // the existing silent unhandled-trigger no-op (BaseBehavior.HandleUnhandledTrigger), same
        // as every other invalid transition. When the machine is willing but Table 8.12 is not,
        // fail loudly: this is a rule violation at the public surface, not an idempotent replay.
        //
        // #198 - this method is also where the manual half of the repetition gate lives, for both
        // an ordinary Stage and the CasePlanModel (which overrides only
        // ManualCompletionCriteriaSatisfied and inherits this unchanged). ORDER MATTERS, and is
        // deliberate: Table 8.12's own criteria are checked FIRST, so the message a caller gets for
        // an ordinary "a required child is still Active" refusal is unchanged, and the repetition
        // message is only ever produced when the repetition really is the sole remaining blocker.
        //
        // Escape hatch / why this cannot wedge a case with no operator recourse:
        //  - ONLY Complete is gated. Terminate, Exit, Suspend, Close and (for the Case)
        //    CaseGrain.Trigger's other transitions are untouched, so a Stage held here can always
        //    still be terminated or the whole Case closed by a Case worker.
        //  - The refusal names the specific blocking child instance ids, so it is actionable
        //    rather than a generic "criteria not met" - and LogCompletionHeldForRepetition emits
        //    the same detail at Warning, so it is diagnosable from logs alone.
        //  - The block clears itself the moment the repetition request is resolved, and EVERY
        //    branch of HandleChildRepeated/SpawnRepetitionOrRefuseCeiling resolves it: spawned
        //    (ChildRepeated), ceiling-refused, refused into a terminal/Failed container, or
        //    unrecognized child. The single branch that does NOT settle - #178's Suspended buffer -
        //    is one where the request is genuinely still pending and blocking IS the correct
        //    answer; it settles when DrainPendingRepetitions later spawns or ceiling-refuses it -
        //    with ONE exception: DrainPendingRepetitions' own catch branch (a transient failure
        //    while draining) deliberately leaves that entry buffered and UNSETTLED rather than
        //    dropping or refusing it. Nothing re-drains it automatically; that child stays terminal
        //    + Repeated + unsettled, holding Complete indefinitely, until another Suspend/Resume
        //    cycle gives DrainPendingRepetitions a further attempt (or an operator falls back to
        //    Terminate). See that catch branch's own remarks.
        //  - Clearing it does not merely stop refusing a MANUAL Complete: resolution re-runs the
        //    automatic criteria too (TryCompleteStageAfterRepetitionResolved), so a Stage that
        //    would have auto-completed does so without needing a human at all.
        //  - RESIDUAL WINDOW, stated honestly rather than hand-waved: a repetition request
        //    published but never delivered to this container leaves the child blocking here. Two
        //    sub-cases, and they do NOT have the same answer under the provider this engine is
        //    actually configured with (Program.cs: AddMemoryStreams("Default") over an in-memory
        //    PubSubStore, while grain journals are durable Azure Blob):
        //      * Handler-level failure inside a live cluster - the pulling agent retries and
        //        redelivers, which is precisely why the #161 redelivery guard exists at all. This
        //        self-heals; the block clears when the retry lands.
        //      * Loss of the queued message itself (silo restart, memory queue gone) - NOT
        //        self-healing here. Memory streams are not durable, so no redelivery is coming,
        //        and the child stays blocking. A durable stream provider would close this; that is
        //        a deployment/transport decision, not something this class can assert on its own.
        //    The failure mode is still strictly better than the defect it replaces: before this
        //    fix, the same lost message meant the repetition silently never happened AND the Stage
        //    completed anyway (silent data loss). Now it is a loud, named, logged refusal on a
        //    Stage that is still Terminate-able. No engine-side "force complete" override exists
        //    today - see docs/03-cmmn-execution-semantics.md for that open question.
        public override async Task Trigger(PlanItemTransition transition)
        {
            if (transition == PlanItemTransition.Complete &&
                StateMachine.CanFire(PlanItemTransition.Complete))
            {
                var children = await GetChildInstances();

                if (!ManualCompletionCriteriaSatisfied(children.Select(x => x.Snapshot).ToArray()))
                {
                    throw new InvalidOperationException(
                        $"stage {Host.Scope}.{Host.InstanceId} does not satisfy Table 8.12's completion criteria: " +
                        (PlanItemDefinition.AutoComplete
                            ? "autoComplete=true requires no Active children and all required children terminal"
                            : "manual completion requires all required children to be in {Disabled, Completed, Terminated, Failed}"));
                }

                var blocking = RepetitionRequestsAwaitingResolution(children);
                if (blocking.Any())
                {
                    LogCompletionHeldForRepetition(blocking, "manual (Table 8.12 Manual Completion)");

                    throw new InvalidOperationException(
                        $"stage {Host.Scope}.{Host.InstanceId} cannot complete yet: child instance(s) " +
                        $"{string.Join(", ", blocking)} declared a repetition (8.6.4) that this stage has " +
                        "neither spawned nor refused. Completing now would drop that instance (#198). This " +
                        "clears itself as soon as the repetition request is processed; Terminate/Exit are " +
                        "not gated by it.");
                }
            }

            await base.Trigger(transition);
        }

        // Live snapshots of every child instance this stage has created (including repetitions),
        // paired with the instance id they were fetched by - the same set HandleChildTransitioned's
        // automatic criteria evaluate, reused by the manual-completion gate so both Table 8.12
        // paths judge the same population.
        //
        // #198 - returns pairs rather than bare snapshots because the instance id is this Stage's
        // key into its OWN settled-repetition-request record (StageBehaviorStore.
        // IsRepetitionSettled) and PlanItemSnapshot carries no instance id. Chosen over adding an
        // InstanceId field to PlanItemSnapshot: the id is already in hand here (it is the very key
        // being iterated), so pairing is free, whereas widening the snapshot would change a
        // serialized public contract that flows out through IPlanItemGrain/ICaseGrain to the API
        // and every test double that constructs one, to serve a need that exists only inside this
        // class.
        private async Task<(string InstanceId, PlanItemSnapshot Snapshot)[]> GetChildInstances()
        {
            var instanceIds = StageStore.Children
                .SelectMany(kvp => kvp.Value.Keys)
                .ToArray();

            var snapshots = await Task.WhenAll(instanceIds
                .Select(piInstanceId => Host.GrainFactory.GetGrain<IPlanItemInternalGrain>(
                        Host.CaseInstanceId,
                        $"{Host.Address}.{piInstanceId}")
                    .GetSnapshot()));

            return instanceIds
                .Zip(snapshots, (instanceId, snapshot) => (InstanceId: instanceId, Snapshot: snapshot))
                .ToArray();
        }

        // #178 - StageBehavior.HandleChildRepeated never used to consult Host.State.PlanItemState
        // at all before this fix: it acted on ANY PlanItemRepetitionCriteriaMetEvent scoped to
        // this Stage, whether the Stage itself was Active, Suspended, or already terminal. The
        // companion half of the defect - BaseBehavior.HandleEnterTerminal unsubscribing only
        // ExitCriteria, leaving a terminated child's EntryCriteria subscription (and therefore
        // this event stream) live - is unchanged by design; the guard belongs here, at the one
        // place that actually decides whether to spawn, not at every possible source of a
        // late/stale trigger. See docs/03-cmmn-execution-semantics.md sections 2-3 for the spec
        // basis each branch below cites.
        private async Task HandleChildRepeated(PlanItemRepetitionCriteriaMetEvent @event, StreamSequenceToken token = null)
        {
            // ignore events that are not direct children of this stage
            if (@event.SourceScope != Host.Address) return;

            // #198 F1 - resolving a request changes the answer to Table 8.12's completion criteria
            // without any child transitioning, so the check HandleChildTransitioned would have run
            // has to be re-run here. Deliberately covers EVERY resolving branch below, including
            // the #161 redelivery early-return: a handler that died between confirming
            // CreateChild/ChildRepeated and re-evaluating completion is exactly the case the
            // stream agent's retry-then-redeliver exists to recover, and returning early on the
            // redelivery without re-evaluating would strand that recovery.
            if (await ResolveRepetitionRequest(@event))
            {
                await TryCompleteStageAfterRepetitionResolved();
            }
        }

        // The body of HandleChildRepeated: decide what this container does with one repetition
        // request. Returns TRUE when the request is no longer pending on this container (spawned,
        // or definitively refused, or already answered by an earlier delivery) and FALSE for the
        // single branch that leaves it genuinely outstanding - #178's Suspended buffer, where
        // holding Table 8.12 completion is still the correct answer and re-evaluating it would be
        // wrong.
        private async Task<bool> ResolveRepetitionRequest(PlanItemRepetitionCriteriaMetEvent @event)
        {
            var child = PlanItemDefinition.PlanItems
                .SingleOrDefault(pi => pi.Id == @event.SourceDefinitionId);

            if (child == null)
            {
                Host.LogWithContext(logger => logger.LogCritical(
                    "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | received child repeat from unknown child {ChildElementDefinitionId}",
                    Host.Definition.GetType().Name,
                    PlanItemDefinition.GetType().Name,
                    Host.Scope,
                    Host.InstanceId,
                    @event.SourceDefinitionId));

                // #198 - a request this container cannot even identify will never be acted on, so
                // it must not hold up Table 8.12 completion forever. Settled, not silently dropped.
                await SettleRepetitionRequest(@event.PlanItemInstanceId, "unknown child definition");
                return true;
            }

            // #161 - Orleans streams are at-least-once; a redelivered
            // PlanItemRepetitionCriteriaMetEvent must be a no-op, not a second physical child at
            // the same repetition index. Ported from SentryStore's OccurrenceToken/IsRedelivery
            // pattern (D5/D11) rather than inventing a new mechanism - see
            // StageBehaviorStore.IsRepetitionRedelivery's remarks for why @event.PlanItemInstanceId
            // (the SOURCE child's own instance id) is a sound, durable idempotency key here: a
            // specific source instance can request exactly one repetition in its lifetime, so a
            // second delivery carrying the identical id is necessarily the SAME request
            // redelivered, never a distinct legitimate repeat (which always arrives from a
            // different, freshly-minted child instance id). Durable across deactivation because
            // #160's trailing ConfirmEvents() below now persists the guard along with everything
            // else this handler raises. This guard is orthogonal to (and runs BEFORE) the #178
            // state switch below: it protects against redelivery of an ALREADY-SPAWNED child,
            // regardless of what this Host's current state is; the Suspended branch below has its
            // own, separate dedupe against redelivery of an already-BUFFERED-but-not-yet-spawned
            // request (StageBehaviorStore.HasPendingRepetition).
            if (StageStore.IsRepetitionRedelivery(@event.PlanItemInstanceId))
            {
                Host.LogWithContext(logger => logger.LogInformation(
                    "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | ignoring redelivered repetition-criteria-met event from source instance {SourceInstanceId} - a child has already been spawned for it",
                    Host.Definition.GetType().Name,
                    PlanItemDefinition.GetType().Name,
                    Host.Scope,
                    Host.InstanceId,
                    @event.PlanItemInstanceId));
                return true;
            }

            // #178 - branch on what THIS Host's own state actually is before ever reaching the
            // ceiling-check/CreateChild machinery below (SpawnRepetitionOrRefuseCeiling), which
            // assumes an Active container. Table 8.9/8.3 dictate three genuinely different
            // responses, not one:
            switch (Host.State.PlanItemState)
            {
                case PlanItemState.Active:
                    break; // the ordinary, expected path - fall through below

                case PlanItemState.Suspended:
                    // docs/03-cmmn-execution-semantics.md section 2 / Table 8.9 + Figure 8.3:
                    // suspension is preserve-and-restore, never discard. Table 8.8's complete row
                    // is Active->Completed only, and 8.5 evaluates entry criteria while Available
                    // - so no repetition trigger can legitimately ORIGINATE inside a genuinely
                    // Suspended Stage. An event observed here was earned BEFORE suspension and is
                    // merely late (this engine's async transport separates the trigger from the
                    // spawn by a message hop - see the spec reference's own implementation note on
                    // 8.6.4), not a modeled scenario. Buffer it - do NOT drop it - and replay it
                    // once this Stage returns to Active (DrainPendingRepetitions, hooked to
                    // Resume/ParentResume in the constructor above).
                    await BufferPendingRepetition(child, @event);
                    return false;

                case PlanItemState.Completed:
                case PlanItemState.Terminated:
                case PlanItemState.Closed:
                    // docs/03-cmmn-execution-semantics.md section 3 / Table 8.9: a terminating
                    // Stage drives every non-terminal child to Terminated via exit BEFORE it,
                    // itself, ever reaches a terminal state - so nothing live can remain inside a
                    // terminal container to legitimately request a repetition. Refuse; no AUDIT
                    // event beyond this log line - unlike Suspended (which preserves real,
                    // earned-but-undelivered work) or Failed (a human-recoverable state where a
                    // silent refusal could be mistaken for "nothing happened"), there is no
                    // legitimate "earned-before" story to flag for a genuinely terminal container.
                    // (#198 added ONE journaled event here, RepetitionRequestSettled below - but
                    // that is bookkeeping the completion gate reads, not an audit record of the
                    // refusal, which is still just this log line.)
                    Host.LogWithContext(logger => logger.LogWarning(
                        "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | refusing repetition of {ChildElementDefinitionId} - this container is {ContainerState} and cannot spawn new children (#178)",
                        Host.Definition.GetType().Name,
                        PlanItemDefinition.GetType().Name,
                        Host.Scope,
                        Host.InstanceId,
                        child.Id,
                        Host.State.PlanItemState));

                    // #198 - refused, and no successor is ever coming (this container is terminal,
                    // and Table 8.9 has already driven every live child out of it). Record that so
                    // the blocking predicate stops holding the request against a completion that
                    // has, in these three states, already happened - and cannot be re-attempted.
                    await SettleRepetitionRequest(@event.PlanItemInstanceId, $"container is {Host.State.PlanItemState}");
                    return true;

                case PlanItemState.Failed:
                    // Semi-terminal and re-activatable (Reactivate -> Active), so not
                    // unambiguously "done" the way Completed/Terminated/Closed are - but the
                    // decisive argument for refusing (not buffering) is spec-structural, not a UX
                    // judgment call: Table 8.9's suspend/resume pair carries EXPLICIT
                    // preserve-and-restore semantics via Figure 8.3's history pseudo-state (the
                    // basis for the Suspended branch above), while the fault/re-activate pair in
                    // Tables 8.6/8.7 carries NO history semantics at all. Buffering-on-suspend
                    // IMPLEMENTS a spec behavior; buffering-on-fault would INVENT one the spec
                    // never describes.
                    //
                    // The practical case for refuse-and-record over silent drop: `fault` is
                    // exactly what the #67 repetition ceiling fires (SpawnRepetitionOrRefuseCeiling
                    // below) - so buffering across a Fault would mean replaying the very requests
                    // that faulted this container the moment it is reactivated, immediately
                    // re-faulting it. A refuse-loop by construction, not a recovery path. Refuse,
                    // same as the terminal branch above, but raise an OBSERVABLE event
                    // (RepetitionRefusedWhileFailed) rather than only a log line, so a silent
                    // refusal is not mistaken for "nothing happened" by whoever investigates the
                    // Failed case.
                    Host.LogWithContext(logger => logger.LogWarning(
                        "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | refusing repetition of {ChildElementDefinitionId} - this container has Failed (#178)",
                        Host.Definition.GetType().Name,
                        PlanItemDefinition.GetType().Name,
                        Host.Scope,
                        Host.InstanceId,
                        child.Id));

                    Host.RaiseEvent(new RepetitionRefusedWhileFailed
                    {
                        RepeatingPlanItemId = child.Id,
                        SourceInstanceId = @event.PlanItemInstanceId,
                        AttemptedRepetition = @event.CurrentRepetition + 1
                    });

                    // #198 - stop holding Table 8.12 completion for this request (see
                    // RepetitionRequestsAwaitingResolution; this exact branch - a Failed container
                    // that is later Reactivated - is reason (a) there for why the record is
                    // journaled rather than derived). Raised alongside RepetitionRefusedWhileFailed
                    // rather than instead of it: that event is the human-facing audit record of WHY,
                    // this one is the machine-readable "stop blocking completion on it".
                    Host.RaiseEvent(new RepetitionRequestSettled
                    {
                        SourceInstanceId = @event.PlanItemInstanceId,
                        Reason = "container has Failed"
                    });

                    await Host.ConfirmEvents();
                    return true;

                default:
                    // Available/Enabled/Disabled/Uninitialized: this Host has no children at all
                    // yet (StageBehavior only ever creates children on entry to Active -
                    // HandleEnterActiveFromStart), so no PlanItemRepetitionCriteriaMetEvent should
                    // be reachable here in practice. Defensive fallback, refusing rather than
                    // falling into ceiling/CreateChild logic that assumes an Active container.
                    Host.LogWithContext(logger => logger.LogCritical(
                        "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | received a repetition-criteria-met event for {ChildElementDefinitionId} while in unexpected state {ContainerState} - refusing (#178)",
                        Host.Definition.GetType().Name,
                        PlanItemDefinition.GetType().Name,
                        Host.Scope,
                        Host.InstanceId,
                        child.Id,
                        Host.State.PlanItemState));

                    // #198 - same reasoning as the unknown-child branch above: refused with no
                    // successor coming, so it must not block completion indefinitely.
                    await SettleRepetitionRequest(@event.PlanItemInstanceId, $"unexpected container state {Host.State.PlanItemState}");
                    return true;
            }

            // SpawnRepetitionOrRefuseCeiling's own return value (ceiling breached or not) is
            // irrelevant here - this is a single-shot event handler, not a loop that needs to decide
            // whether to keep going; only DrainPendingRepetitions below consumes it. EITHER outcome
            // resolves the request (spawned, or ceiling-refused and settled), hence the unconditional
            // true.
            await SpawnRepetitionOrRefuseCeiling(child, @event.PlanItemInstanceId, @event.CurrentRepetition + 1);
            return true;
        }

        // #198 - shared write side of the "this container has definitively dealt with that
        // repetition request" record, for the branches that refuse with nothing further to come.
        // The SPAWNED path does not go through here: ChildRepeated (#161) already carries the same
        // SourceInstanceId and StageBehaviorStore.Apply(ChildRepeated) settles from it, so adding a
        // second event there would journal the same fact twice.
        //
        // Confirms immediately, Bug #61 discipline: every call site returns straight afterwards, so
        // there is no later confirm for this raise to ride on and an unconfirmed settle would be
        // lost on deactivation - re-blocking a completion whose request was already refused, which
        // is exactly the wedge this record exists to prevent.
        private Task SettleRepetitionRequest(string sourceInstanceId, string reason)
        {
            Host.RaiseEvent(new RepetitionRequestSettled
            {
                SourceInstanceId = sourceInstanceId,
                Reason = reason
            });

            return Host.ConfirmEvents();
        }

        // #178 - write side of the Suspended buffer (see HandleChildRepeated's Suspended branch).
        // Hazard 1: dedupe on the SAME SourceInstanceId the #161 guard uses, via
        // StageBehaviorStore.HasPendingRepetition, so a redelivered PlanItemRepetitionCriteriaMetEvent
        // that arrives again while still Suspended cannot queue a second, duplicate buffered
        // request for the same logical repetition.
        private async Task BufferPendingRepetition(Interfaces.Model.PlanItem child, PlanItemRepetitionCriteriaMetEvent @event)
        {
            if (StageStore.HasPendingRepetition(@event.PlanItemInstanceId))
            {
                Host.LogWithContext(logger => logger.LogInformation(
                    "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | ignoring redelivered repetition-criteria-met event from source instance {SourceInstanceId} - already buffered while suspended, awaiting resume",
                    Host.Definition.GetType().Name,
                    PlanItemDefinition.GetType().Name,
                    Host.Scope,
                    Host.InstanceId,
                    @event.PlanItemInstanceId));
                return;
            }

            var nextRepetition = @event.CurrentRepetition + 1;

            Host.LogWithContext(logger => logger.LogInformation(
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | this container is Suspended - buffering repetition {NextRepetition} of {ChildElementDefinitionId} for replay on resume",
                Host.Definition.GetType().Name,
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                nextRepetition,
                child.Id));

            Host.RaiseEvent(new RepetitionBuffered
            {
                SourceInstanceId = @event.PlanItemInstanceId,
                PlanItemDefinitionId = child.Id,
                NextRepetition = nextRepetition
            });

            // #178, matching the #160 Bug #61 discipline used throughout this class: this raise
            // has no other confirm to ride on, so it must be confirmed explicitly or it sits
            // unconfirmed in TentativeState indefinitely and the buffered request is lost on
            // deactivation.
            await Host.ConfirmEvents();
        }

        // #178 - replays StageStore.PendingRepetitions once this Host has returned to Active (see
        // the Resume/ParentResume entry-action registration in the constructor above, and
        // CasePlanModelBehavior's own Reactivate registration for the CasePlanModel's equivalent -
        // that class's own remarks explain why the CasePlanModel needs a source-state-guarded hook
        // on a DIFFERENT trigger rather than reusing Resume/ParentResume directly). Protected (not
        // private) so CasePlanModelBehavior can invoke it.
        //
        // No "is this Host still Active" re-check at the top of the loop: this method never awaits
        // anything that could let another message interleave on this activation (this grain class
        // is not [Reentrant], so nothing else can run here until this whole Trigger(...) call
        // completes) - the only way this Host's own state changes DURING this loop is the
        // ceilingBreached Fault below, which is handled explicitly via that return value, not by
        // re-reading Host.State (see its remarks for why a re-read would not even catch it).
        protected async Task DrainPendingRepetitions()
        {
            var faultedMidDrain = false;

            // Snapshot: PendingRepetitions is mutated (entries removed) as this loop progresses -
            // iterate a stable copy, not the live collection.
            foreach (var pending in StageStore.PendingRepetitions.ToList())
            {
                // Hazard (review round 2) - the drain must honor the SAME #161 redelivery guard
                // the live path checks in HandleChildRepeated. Reachable: an earlier drain
                // iteration (of THIS same entry, on a prior Suspend/Resume cycle) confirmed
                // CreateChild + ChildRepeated - which durably records the guard - and then this
                // activation died before the FOLLOWING RepetitionBufferDrained was confirmed. The
                // guard is durable; the still-buffered entry is ALSO durable (the same
                // ConfirmEvents() call that would have removed it never ran) - so without this
                // check, the next drain would spawn a SECOND child at the same repetition index.
                // Checked before ever touching CreateChild, mirroring HandleChildRepeated's own
                // ordering.
                if (StageStore.IsRepetitionRedelivery(pending.SourceInstanceId))
                {
                    Host.LogWithContext(logger => logger.LogInformation(
                        "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | ignoring buffered repetition request from source instance {SourceInstanceId} - a child was already spawned for it by an earlier, unconfirmed-until-now drain",
                        Host.Definition.GetType().Name,
                        PlanItemDefinition.GetType().Name,
                        Host.Scope,
                        Host.InstanceId,
                        pending.SourceInstanceId));

                    Host.RaiseEvent(new RepetitionBufferDrained { SourceInstanceId = pending.SourceInstanceId });
                    await Host.ConfirmEvents();
                    continue;
                }

                try
                {
                    var child = PlanItemDefinition.PlanItems
                        .SingleOrDefault(pi => pi.Id == pending.PlanItemDefinitionId);

                    var ceilingBreached = false;

                    if (child == null)
                    {
                        // The definition changed out from under a still-buffered request (should
                        // not happen in practice - PlanItemDefinition is immutable for the
                        // lifetime of this Host - but defensive, matching HandleChildRepeated's
                        // own unknown-child handling). Drop it: there is nothing left to spawn.
                        // Not a ceiling breach - does not stop the rest of the batch from
                        // draining.
                        Host.LogWithContext(logger => logger.LogCritical(
                            "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | buffered repetition request references unknown child {ChildElementDefinitionId} - dropping",
                            Host.Definition.GetType().Name,
                            PlanItemDefinition.GetType().Name,
                            Host.Scope,
                            Host.InstanceId,
                            pending.PlanItemDefinitionId));

                        // #198 - dropped means definitively refused: nothing will ever spawn for
                        // it, so it must not keep blocking Table 8.12 completion. Confirmed below
                        // together with this entry's RepetitionBufferDrained.
                        Host.RaiseEvent(new RepetitionRequestSettled
                        {
                            SourceInstanceId = pending.SourceInstanceId,
                            Reason = "buffered request references an unknown child definition"
                        });
                    }
                    else
                    {
                        // Hazard 2: the #67 ceiling still applies on replay - this goes through
                        // the exact same check-then-spawn path a live (non-buffered) request
                        // would.
                        ceilingBreached = await SpawnRepetitionOrRefuseCeiling(child, pending.SourceInstanceId, pending.NextRepetition);
                    }

                    // Drain (remove) this entry regardless of whether it was spawned or refused
                    // by the ceiling: a ceiling-refused buffered request is not retried, matching
                    // the live ceiling path's own no-retry semantics (see
                    // SpawnRepetitionOrRefuseCeiling's remarks) - re-attempting the SAME buffered
                    // request on a future resume would just repeat the identical refusal (see
                    // also the #178 decision recorded in this class's constructor and
                    // CasePlanModelBehavior's, on why neither ordinary Stage/Task nor the
                    // CasePlanModel auto-drains on a Failed->Reactivate recovery).
                    Host.RaiseEvent(new RepetitionBufferDrained { SourceInstanceId = pending.SourceInstanceId });
                    await Host.ConfirmEvents();

                    // Self-review hazard - stop draining the REST of this batch the moment one
                    // entry breaches the ceiling, using SpawnRepetitionOrRefuseCeiling's OWN
                    // return value, not a subsequent Host.State.PlanItemState re-read: this
                    // method runs as an OnEntryFromAsync action of the Resume/ParentResume/
                    // Reactivate transition still being dispatched, so the Fault fired inside
                    // SpawnRepetitionOrRefuseCeiling is REENTRANT - Stateless (FiringMode.Queued)
                    // enqueues it rather than applying it immediately, and will not drain that
                    // queue until this ENTIRE outer transition finishes unwinding (see that
                    // method's remarks). Host.State.PlanItemState would therefore still read
                    // Active here even though the container is, semantically, already faulted -
                    // relying on it would let a SECOND buffered entry spawn (or hit its own,
                    // redundant ceiling refusal) into what is effectively already a faulted
                    // container within the very same drain pass.
                    if (ceilingBreached)
                    {
                        faultedMidDrain = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    // Should-fix (review round 2) - this method runs as an entry action of the
                    // transition ITSELF still being dispatched (see the ceilingBreached remarks
                    // just above for the same reentrancy fact). Stateless commits the destination
                    // state and runs OnTransitionedAsync/HandleTransitioned BEFORE entry actions
                    // run, so an exception escaping this loop escapes this entry action, escapes
                    // the outer FireAsync call, and leaves this Host stuck: the transition itself
                    // already happened and cannot be "retried" by firing it again, and - unlike
                    // the live (non-buffered) path - there is no Orleans stream-agent
                    // retry-then-redeliver to eventually re-invoke this drain. So: catch here,
                    // log, leave THIS entry buffered (do not raise RepetitionBufferDrained, do not
                    // rethrow), and let the REST of the batch still get a chance - a transient
                    // failure (e.g. a timeout) spawning one buffered request must not permanently
                    // strand every other request queued behind it, nor silently swallow the fact
                    // that this one needs another attempt later.
                    //
                    // Note this branch is the exception to Trigger's doc comment claim that every
                    // buffered request eventually settles: this entry stays buffered and UNSETTLED,
                    // and nothing re-invokes DrainPendingRepetitions for it on its own - only a
                    // future Suspend/Resume cycle gives it another attempt. With #198's gate in
                    // place, the owning child stays terminal + Repeated + unsettled and holds
                    // Complete indefinitely in the meantime; recourse is Terminate or a manual
                    // Suspend/Resume.
                    Host.LogWithContext(logger => logger.LogError(ex,
                        "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | draining buffered repetition request from source instance {SourceInstanceId} threw - leaving it buffered for a future drain attempt",
                        Host.Definition.GetType().Name,
                        PlanItemDefinition.GetType().Name,
                        Host.Scope,
                        Host.InstanceId,
                        pending.SourceInstanceId));
                }
            }

            if (faultedMidDrain)
            {
                await SettleStrandedPendingRepetitions();
                return;
            }

            // #198 F1 - the drain has just resolved (spawned, or ceiling-refused) every entry it
            // touched, and resolving a request re-opens Table 8.12 exactly as it does on the live
            // path. Skipped entirely on the ceiling-breach path above: there the container is
            // semantically already Faulted, and Host.State.PlanItemState is not yet a reliable
            // witness of that (see the ceilingBreached remarks in the loop), so evaluating
            // completion here could journal an AutoComplete/FullyComplete criteria event and
            // enqueue a Complete behind the Fault the same pass already fired.
            await TryCompleteStageAfterRepetitionResolved();
        }

        // #198 F2 - the #67 ceiling Faulting this Host mid-drain strands every entry queued behind
        // the one that breached it (the `break` above, deliberately - see its remarks). Those
        // entries are never replayed: neither this class (no Reactivate hook at all) nor
        // CasePlanModelBehavior (its hook is guarded to transition.Source == Suspended) drains on a
        // Failed->Reactivate recovery, both by design, because replaying them would immediately
        // re-fault the container. So after a human reactivates, their requesting children are still
        // terminal + Repeated + unsettled, and RepetitionRequestsAwaitingResolution would hold
        // Complete on the recovered container FOREVER - a wedge with no recourse but Terminate,
        // which is the one outcome this whole design exists to avoid.
        //
        // Settle them, so they stop holding completion. Deliberately settle ONLY - the entries stay
        // buffered, untouched and unattempted, exactly as the `break` left them: this method's job
        // is to stop the wedge, not to decide that a request queued behind an unrelated ceiling
        // breach may never be spawned. If a later Suspend/Resume cycle does drain one and it
        // spawns, ChildRepeated settles it again (the set is monotonic, so that is a no-op) - which
        // is why RepetitionRequestSettled is documented as "this container is no longer holding
        // Table 8.12 completion for this request" rather than the stronger "no successor can ever
        // appear".
        private async Task SettleStrandedPendingRepetitions()
        {
            var stranded = StageStore.PendingRepetitions
                .Where(pending => !StageStore.IsRepetitionSettled(pending.SourceInstanceId))
                .ToList();

            if (!stranded.Any()) return;

            foreach (var pending in stranded)
            {
                Host.LogWithContext(logger => logger.LogWarning(
                    "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | buffered repetition request from source instance {SourceInstanceId} was stranded by a #67 ceiling breach earlier in this drain - it stays buffered but is settled, so it cannot hold Table 8.12 completion once this container is reactivated (#198)",
                    Host.Definition.GetType().Name,
                    PlanItemDefinition.GetType().Name,
                    Host.Scope,
                    Host.InstanceId,
                    pending.SourceInstanceId));

                Host.RaiseEvent(new RepetitionRequestSettled
                {
                    SourceInstanceId = pending.SourceInstanceId,
                    Reason = "stranded by a #67 ceiling breach earlier in the same drain"
                });
            }

            // Bug #61 discipline, as everywhere else in this class: this batch has no later confirm
            // to ride on (the caller returns immediately), and an unconfirmed settle would be lost
            // on deactivation - re-opening the very wedge it exists to close.
            await Host.ConfirmEvents();
        }

        // Shared tail of the live (HandleChildRepeated, Active) and replayed (DrainPendingRepetitions,
        // on resume) repetition paths: enforce the #67 ceiling, then either spawn the child or
        // refuse and Fault. Factored out by #178 so the ceiling can never drift between the two
        // call sites - a buffered request must be held to exactly the same limit a live one is.
        // Returns true when the ceiling was breached (refused, Host faulted or fault-attempted),
        // false when the child was actually spawned - DrainPendingRepetitions' caller uses this
        // (NOT a post-hoc read of Host.State.PlanItemState - see that method's remarks for why)
        // to know whether it is safe to keep draining further buffered entries in the same pass.
        private async Task<bool> SpawnRepetitionOrRefuseCeiling(Interfaces.Model.PlanItem child, string sourceInstanceId, int nextRepetition)
        {
            // ADO #67 - Wayfinder ENGINE EXTENSION (RepetitionGuardOptions), NOT CMMN spec
            // surface.
            // ~~~~~
            // 8.6.4 imposes no upper bound on how many times a repeating item may re-spawn - see
            // RepetitionGuardOptions' remarks for the #19 foot-gun this closes. Faulting THIS
            // container (Host) rather than the repeating child is deliberate: the child that
            // published PlanItemRepetitionCriteriaMetEvent is either already terminal
            // (Completed/Terminated, for the no-entry-criteria path - BaseBehavior.
            // TryRepeatOnCompleteOrTerminate) or still legitimately running (the entry-criterion
            // OnPart path - HandleSentrySatisfied), and PlanItemStateMachine.
            // ConfigureForStageOrTask permits no outgoing transition at all from Completed/
            // Terminated for an ordinary Stage/Task - neither is ever a legal Fault target. This
            // Stage/CasePlanModel is the one actually issuing the runaway CreateChild calls and
            // is still Active when the ceiling is hit (Active legally Permits(Fault, Failed) in
            // both ConfigureForStageOrTask and ConfigureForCasePlanModel) - the only always-valid
            // target, and the entity actually responsible for the spawn.
            // #161 scope note: this ceiling branch deliberately does NOT record the redelivery
            // guard. A redelivery that lands here re-raises RepetitionCeilingExceeded and
            // re-attempts Fault (a no-op once already Failed - CanFire(Fault) is false, so it
            // falls to the plain ConfirmEvents() branch below) - durably-confirmed audit/journal
            // noise, not a duplicate CHILD, which is the defect #161 closes. Recording the guard
            // here would mean either reusing ChildRepeated (misleading - no child was created) or
            // adding a second, differently-named guard event for one rare, already-Faulted-host
            // edge case; not worth the added surface for a cosmetic duplicate-event concern on a
            // path that already terminates the container.
            if (nextRepetition >= _repetitionCeiling)
            {
                Host.LogWithContext(logger => logger.LogError(
                    "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | repetition ceiling {Ceiling} reached for child {ChildElementDefinitionId} (refusing repetition {NextRepetition}) - this is a Wayfinder engine safety extension (#67), not CMMN 1.1 spec behavior. Faulting this container instead of spawning further instances.",
                    Host.Definition.GetType().Name,
                    PlanItemDefinition.GetType().Name,
                    Host.Scope,
                    Host.InstanceId,
                    _repetitionCeiling,
                    child.Id,
                    nextRepetition));

                Host.RaiseEvent(new RepetitionCeilingExceeded
                {
                    RepeatingPlanItemId = child.Id,
                    AttemptedRepetition = nextRepetition,
                    Ceiling = _repetitionCeiling
                });

                // #198 - stop holding Table 8.12 completion for this request (see
                // RepetitionRequestsAwaitingResolution). Note this branch still does NOT record the
                // #161 redelivery guard - the two records mean different things, per the scope note
                // just above.
                //
                // Raised, not raised-and-confirmed via SettleRepetitionRequest: this branch does
                // not return immediately, and whichever of the two paths below runs confirms
                // everything queued here along with RepetitionCeilingExceeded (see the long note
                // in the CanFire branch on WHO commits on which call path).
                Host.RaiseEvent(new RepetitionRequestSettled
                {
                    SourceInstanceId = sourceInstanceId,
                    Reason = $"#67 repetition ceiling {_repetitionCeiling} reached"
                });

                if (StateMachine.CanFire(PlanItemTransition.Fault))
                {
                    // WHO actually commits the RepetitionCeilingExceeded event raised just above
                    // depends on which caller reached here - do not assume it is always
                    // HandleTransitioned's own confirm (review round 2: the original comment here
                    // claimed that unconditionally, which is only true on ONE of the two paths):
                    //
                    // - LIVE path (called directly from HandleChildRepeated, NOT reentrant): this
                    //   FireAsync(Fault) runs to completion synchronously within this await.
                    //   HandleTransitioned (BaseBehavior) raises Transitioned and confirms as part
                    //   of that - the SAME ConfirmEvents() call commits RepetitionCeilingExceeded,
                    //   per the same Publish-before-Repeated/Bug #61 ordering discipline used
                    //   elsewhere in this class.
                    // - REPLAYED path (called from DrainPendingRepetitions, itself an
                    //   OnEntryFromAsync action of the Resume/ParentResume/Reactivate transition
                    //   still being dispatched): this FireAsync(Fault) call is REENTRANT -
                    //   Stateless (FiringMode.Queued, its default - see PlanItemStateMachine's own
                    //   remarks on the equivalent Create->Start reentrant case) ENQUEUES it rather
                    //   than running it to completion immediately, and does not drain that queue
                    //   until the OUTERMOST FireAsync call itself finishes unwinding.
                    //   HandleTransitioned's confirm does NOT run here in that case - it is
                    //   DrainPendingRepetitions' OWN trailing ConfirmEvents() call (immediately
                    //   after this method returns) that actually commits
                    //   RepetitionCeilingExceeded. Host.State.PlanItemState also will NOT yet read
                    //   Failed immediately after this await returns on this path, even though the
                    //   await itself completes normally - the bool return below, not a subsequent
                    //   Host.State read, is what DrainPendingRepetitions relies on to stop
                    //   draining further entries in the same pass.
                    //
                    // Correct today by ordering, on both paths - not by anything that enforces it.
                    // If either call site's confirm is ever reordered or removed, this event could
                    // silently stop being committed on one of the two paths without a compiler or
                    // test failure pointing here; keep this comment truthful if that happens.
                    await StateMachine.FireAsync(PlanItemTransition.Fault);
                }
                else
                {
                    // Host cannot Fault from its current state (unexpected, but not this
                    // method's call to force) - confirm explicitly so the ceiling event is not
                    // left sitting unconfirmed in TentativeState indefinitely (Bug #61).
                    await Host.ConfirmEvents();
                }

                return true;
            }

            // Bug #62 root cause (fixed, #19): this template previously declared SIX placeholders
            // but passed FIVE arguments (the long-standing CA2017 warning). Message-template
            // renderers that format eagerly (MEL's FormattedLogValues via String.Format - e.g.
            // the console logger, or Orleans.TestingHost's file logger) THROW FormatException on
            // the mismatch, MEL rethrows it as AggregateException, and since this runs inside a
            // stream-delivery turn the fault was swallowed by the streaming agent's
            // retry-then-drop - so HandleChildRepeated appeared "never reached" and repetitions
            // silently never spawned. The subscription wiring was correct the whole time.
            Host.LogWithContext(logger => logger.LogInformation(
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | instantiating repetition {Repetition} of {ChildElementDefinitionId}",
                Host.Definition.GetType().Name,
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                nextRepetition,
                child.Id));

            // #160/#161 ordering - deliberately AFTER CreateChild, not before: RaiseEvent applies
            // synchronously to TentativeState, so recording the redelivery guard BEFORE the child
            // actually exists would let a partial CreateChild failure (its own grain calls throw
            // partway through) leave the guard set with nothing to show for it - the handler
            // throws, ConfirmEvents never runs, the stream agent's retry-then-redeliver (see the
            // Bug #62 remarks above) is exactly the recovery path that would otherwise re-create
            // the child, but IsRepetitionRedelivery would now report a false positive and the
            // retry would silently no-op. Worst case: the entry-criterion OnPart path already
            // unsubscribed before publishing, and the no-entry-criteria path's source is already
            // terminal - neither can ever re-request, so one transient CreateChild failure would
            // have permanently stopped that repetition. Recording the guard only once the child
            // demonstrably exists means a genuine partial-failure retry can at worst DUPLICATE
            // (CreateChild runs again, spawning a second child) rather than silently and
            // permanently losing the repetition - a visible, recoverable failure mode beats an
            // invisible, permanent one, and duplication here is a strict subset of the #161 defect
            // this same guard already closes for the common (fully-succeeded, then redelivered)
            // case.
            await CreateChild(child, nextRepetition);

            // #160 - SourceInstanceId records this repetition request's idempotency key (see the
            // redelivery guard above) so a later redelivery of the SAME event can recognize
            // itself and no-op instead of spawning a duplicate child. Raised AFTER CreateChild
            // (see remarks above) and confirmed together with it below.
            Host.RaiseEvent(new ChildRepeated
            {
                SourceInstanceId = sourceInstanceId
            });

            // #160 - CreateChild raises ChildCreated (and the ChildRepeated above) on THIS
            // Stage/CasePlanModel's own TentativeState; nothing else in this handler's call chain
            // confirms them, so without this they would sit unconfirmed indefinitely and be lost
            // entirely if this activation deactivates before some later, unrelated event on this
            // same host happens to confirm them (Bug #61 class - see HandleEnterActiveFromStart's
            // remarks for the same discipline applied to the equivalent first-creation path).
            // Confirmed once after CreateChild completes, matching that same batching convention.
            await Host.ConfirmEvents();

            return false;
        }

        private async Task CreateChild(Interfaces.Model.PlanItem child, int repetition = 0)
        {
            var childInstanceId = ShortGuid.NewGuid();

            Host.LogWithContext(logger => logger.LogInformation(
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | creating instance {ChildElementInstanceId} of child {ChildElementDefinitionId}",
                Host.Definition.GetType().Name,
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                childInstanceId,
                child.Id));

            var childGrain = Host.GrainFactory.GetGrain<IPlanItemInternalGrain>(
                Host.CaseInstanceId,
                $"{Host.Address}.{childInstanceId}");

            // Host.DefinitionId: this Stage's own definition id, i.e. the new child's PARENT
            // definition id - threaded through so the child's BaseBehavior.Activate can key its
            // parent-transition subscription on the same stream this Stage's transitions publish
            // on (#63).
            // Host.DefinitionScope: this Stage's own full DEFINITION-scope path, threaded through
            // so the child's CaseDefinitionGrain.GetPlanItemDefinition lookup searches from the
            // correct definition-tree position instead of this Stage's runtime instance address
            // (#65).
            await childGrain.DefineRepetition(Host.State.CaseDefinitionId, child, repetition, Host.DefinitionId, Host.DefinitionScope);
            await childGrain.Trigger(PlanItemTransition.Create);

            await Task.WhenAll(
                Host.SubscribeTo<PlanItemTransitionedEvent>(
                    child.Id,
                    HandleChildTransitioned,
                    StreamFlags.Create),
                Host.SubscribeTo<PlanItemRepetitionCriteriaMetEvent>(
                    child.Id,
                    HandleChildRepeated,
                    StreamFlags.Create));

            Host.RaiseEvent(new ChildCreated
            {
                PlanItemId = child.Id,
                PlanItemInstanceId = childInstanceId,
                Repetition = repetition
            });
        }
    }
}
