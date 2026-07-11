using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    public class MilestoneBehavior : BaseBehavior<Milestone>
    {
        public MilestoneBehavior(IBehaviorHost host, Milestone planItemDefinition, IPlanItemStateMachine stateMachine) :
            base(host, planItemDefinition, stateMachine)
        {
            // 8.10 - EventListener and Milestone instance states
            // ~~~~~
            // In this state an EventListener instance is waiting for the event to occur.
            // A Milestone instance in this state is waiting for the Sentry (as entry
            // criterion) to be satisfied.
            StateMachine.Configure(PlanItemState.Available)
                .OnEntryFromAsync(PlanItemTransition.Create, HandleEnterAvailableFromCreate);
        }
        
        // 8.11 - EventListener and Milestone instance transitions
        // ~~~~~
        // Create: Transition to the initial state (Available) when an EventListener
        // or Milestone instance is created. For a Milestone instance, the
        // RepetitionRule Boolean expression MUST be evaluated in this transition.
        // For a Milestone instance, the RequiredRule Boolean expression MUST be
        // evaluated in this transition, and its Boolean value SHOULD be maintained
        // for the rest of the life of the Milestone instance.
        // ==============================
        // 8.4 - Case, EventListener, Milestone, Stage, and Task instance states
        // ~~~~~
        // Available: The instance is waiting for a Sentry to become TRUE or for an
        // event to occur, so that the instance can progress to its primary purpose
        // (e.g., become Active or Enabled).
        // ==============================
        // 5.24 - PlanItem attributes
        // ~~~~~
        // A PlanItem that is defined by an EventListener or Milestone MUST NOT have
        // exitCriteriaRefs.
        // Sequential, not Task.WhenAll - see StageBehavior.HandleEnterAvailableFromCreate's
        // remarks (#19). A Milestone has no EnableOrStart cascade, but the same
        // raise/confirm interleaving hazard exists between the two rule evaluations
        // (EvaluateRequiredRule confirms internally while EvaluateRepetitionRule is
        // mid-flight to the expression grain).
        private async Task HandleEnterAvailableFromCreate()
        {
            await EvaluateRepetitionRule(discard: true);
            await EvaluateRequiredRule();
            await SubscribeToCriteria(x => x.EntryCriteria, StreamFlags.Create);
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
                .EntryCriteria
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

            if (StateMachine.CanFire(PlanItemTransition.Occur))
            {
                await StateMachine.FireAsync(PlanItemTransition.Occur);
            }
            // 8.6.4 RepetitionRule
            // ~~~~~
            // (after transitioning from Available), every time an entry criterion with an OnPart is satisfied
            // the RepetitionRule’s condition is re-evaluated and if it evaluates to TRUE, a new instance of the
            // Task, Stage, or Milestone is created and transition to Available
            else if (Host.State.PlanItemState.IsTerminal() &&
                     @event.OnPartOccurred &&
                     await EvaluateRepetitionRule())
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
                    transition = PlanItemTransition.Suspend;
                    break;
                }
                case PlanItemTransition.Resume:
                case PlanItemTransition.ParentResume:
                // Table 8.6 re-activate + Table 8.9 note (2) (#63 D8 carve-out): see
                // StageBehavior.HandleParentTransitioned's remarks - the Case leaves Suspended via
                // Reactivate, not Resume. A Milestone's Suspended->Available Resume permit is
                // unconditional (ConfigureForMilestoneOrEventListener has no ParentSuspendState
                // gating - Available is the only pre-Suspend state), and StateMachine.CanFire
                // below still no-ops for a child not currently Suspended.
                case PlanItemTransition.Reactivate:
                {
                    Host.RaiseEvent(new ParentResumed());
                    transition = PlanItemTransition.Resume;
                    break;
                }
                case PlanItemTransition.Exit:
                case PlanItemTransition.Terminate:
                {
                    Host.RaiseEvent(new ParentTerminated());
                    transition = PlanItemTransition.ParentTerminate;
                    break;
                }
            }

            if (transition.HasValue && StateMachine.CanFire(transition.Value))
            {
                await StateMachine.FireAsync(transition.Value);
            }
        }
    }
}
