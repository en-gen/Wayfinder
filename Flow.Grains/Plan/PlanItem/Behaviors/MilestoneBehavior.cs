using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Interfaces.Model;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    public class MilestoneBehavior : BaseBehavior<Milestone>
    {
        public MilestoneBehavior(IBehaviorHost host, Milestone planItemDefinition) :
            base(host, planItemDefinition)
        {
            // 8.10 - EventListener and Milestone instance states
            // ~~~~~
            // In this state an EventListener instance is waiting for the event to occur.
            // A Milestone instance in this state is waiting for the Sentry (as entry
            // criterion) to be satisfied.
            Host.StateMachine.Configure(PlanItemState.Available)
                .OnEntryFromAsync(PlanItemTransition.Create, HandleEnterAvailableFromCreate);
        }

        public override Task Define() => Task.CompletedTask;

        public override Task Activate() => Task.WhenAll(
            Host.SubscribeTo<PlanItemTransitionedEvent>(
                Host.ParentId,
                HandleParentTransitioned,
                StreamFlags.Create | StreamFlags.Resume),
            Task.WhenAll(Host.Definition.EntryCriteria
                .Select(c => Host.SubscribeTo<SentrySatisfiedEvent>(
                    c.SentryRef,
                    HandleSentrySatisfied,
                    StreamFlags.Resume))));

        public override Task<bool> IsUserCompletable() => Task.FromResult(false);

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
        private Task HandleEnterAvailableFromCreate()
        {
            return Task.WhenAll(
                EvaluateRepetitionRule(),
                EvaluateRequiredRule(),
                Task.WhenAll(Host.Definition.EntryCriteria
                    .Select(c => Host.SubscribeTo<SentrySatisfiedEvent>(
                        c.SentryRef,
                        HandleSentrySatisfied,
                        StreamFlags.Create))));
        }

        private async Task HandleParentTransitioned(PlanItemTransitionedEvent @event, StreamSequenceToken token = null)
        {
            switch (@event.StandardEvent)
            {
                case PlanItemTransition.ParentSuspend:
                {
                    Host.RaiseEvent(new ParentSuspended());
                    if (Host.StateMachine.CanFire(PlanItemTransition.Suspend))
                    {
                        await Host.StateMachine.FireAsync(PlanItemTransition.Suspend);
                    }
                    break;
                }
                case PlanItemTransition.ParentResume:
                {
                    Host.RaiseEvent(new ParentResumed());
                    if (Host.StateMachine.CanFire(PlanItemTransition.Resume))
                    {
                        await Host.StateMachine.FireAsync(PlanItemTransition.Resume);
                    }
                    break;
                }
                case PlanItemTransition.ParentTerminate:
                {
                    Host.RaiseEvent(new ParentTerminated());
                    if (Host.StateMachine.CanFire(PlanItemTransition.ParentTerminate))
                    {
                        await Host.StateMachine.FireAsync(PlanItemTransition.ParentTerminate);
                    }
                    break;
                }
            }
        }

        protected override async Task HandleSentrySatisfied(SentrySatisfiedEvent @event, StreamSequenceToken token = null)
        {
            // ignore events that not in this plan item's hierarchy
            if (!Host.Scope.StartsWith(@event.SourceScope)) return;
            var criterion = Host.Definition
                .EntryCriteria
                .Union<Criterion>(Host.Definition.ExitCriteria)
                .SingleOrDefault(x => x.SentryRef.Equals(@event.SourceId));

            // ignore events in our scope, but not our concern
            if (criterion == null) return;
            // ignore entry criteria if already repeated
            // just being defensive - should never happen as entry subs are removed on repeat
            if (criterion is EntryCriterion && Host.State.Repeated) return;

            Host.LogWithContext(logger => logger.LogInformation(
                "{ElementType} {ElementScope}.{ElementId} {CriterionType} {CriterionId} satisfied by sentry {SentryRef}.  OnPart: {OnPartOccurred}",
                Host.Definition.GetType().Name,
                Host.Scope,
                Host.Id,
                criterion.GetType().Name,
                criterion.Id,
                @event.SourceId,
                @event.OnPartOccurred));

            if (criterion is EntryCriterion)
            {
                Host.RaiseEvent(new EntryCriterionSatisfied
                {
                    SourceScope = @event.SourceScope,
                    SourceId = @event.SourceId,
                    OnPartOccurred = @event.OnPartOccurred
                });
                if (Host.StateMachine.CanFire(PlanItemTransition.Occur))
                {
                    await Host.StateMachine.FireAsync(PlanItemTransition.Occur);
                }
                else if (Host.State.PlanItemState.IsTerminal() &&
                         @event.OnPartOccurred &&
                         await EvaluateRepetitionRule())
                {
                    // TODO: tell host to repeat
                }
            }
            else if (criterion is ExitCriterion)
            {
                Host.RaiseEvent(new ExitCriterionSatisfied
                {
                    SourceId = @event.SourceId,
                    OnPartOccurred = @event.OnPartOccurred
                });
            }

            await Host.ConfirmEvents();
        }
    }
}
