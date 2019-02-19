using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Executables;
using Flow.Grains.Expressions;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    public abstract class BaseBehavior<TPlanItemDefinition> : IPlanItemBehavior
        where TPlanItemDefinition : PlanItemDefinition
    {
        protected IBehaviorHost Host { get; }
        protected TPlanItemDefinition PlanItemDefinition { get; }
        protected IPlanItemStateMachine StateMachine { get; }

        protected abstract Task HandleSentrySatisfied(SentrySatisfiedEvent @event, StreamSequenceToken token = null);
        protected abstract Task HandleParentTransitioned(PlanItemTransitionedEvent @event, StreamSequenceToken token = null);

        protected BaseBehavior(IBehaviorHost host, TPlanItemDefinition planItemDefinition, IPlanItemStateMachine stateMachine)
        {
            Host = host ?? throw new ArgumentNullException(nameof(host));
            PlanItemDefinition = planItemDefinition ?? throw new ArgumentNullException(nameof(planItemDefinition));
            StateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));

            StateMachine.Configure(PlanItemState.Completed)
                .OnEntryAsync(HandleEnterTerminal);

            StateMachine.Configure(PlanItemState.Terminated)
                .OnEntryAsync(HandleEnterTerminal);

            StateMachine.OnTransitionedAsync(HandleTransitioned);

            StateMachine.OnUnhandledTriggerAsync(HandleUnhandledTrigger);
        }

        protected virtual Task Define() => Task.CompletedTask;

        public virtual Task Activate() =>
            Task.WhenAll(
                SubscribeToCriteria(x => x.EntryCriteria, StreamFlags.Resume),
                SubscribeToCriteria(x => x.ExitCriteria, StreamFlags.Resume),
                Host.SubscribeTo<PlanItemTransitionedEvent>(Host.ParentInstanceId, HandleParentTransitioned, StreamFlags.Create | StreamFlags.Resume),
                Host.State.Defined
                    ? Define()
                    : Task.CompletedTask);

        public Task Trigger(PlanItemTransition transition) => StateMachine.FireAsync(transition);

        private Task HandleEnterTerminal() => Task.WhenAll(Host.Definition.ExitCriteria
            .Select(c => Host.UnsubscribeFrom<SentrySatisfiedEvent>(c.SentryRef)));

        private async Task HandleTransitioned(PlanItemStateMachine.Transition transition)
        {
            Host.LogWithContext(logger => logger.LogInformation(
                "{ElementType} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | processed transition {PreviousState} × {StandardEvent} = {CurrentState}",
                Host.Definition.GetType().Name,
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                transition.Trigger,
                transition.Source,
                transition.Destination));

            Host.RaiseEvent(new Transitioned
            {
                Source = transition.Source,
                Destination = transition.Destination,
                Trigger = transition.Trigger
            });
            await Host.ConfirmEvents();

            await Host.Publish(new PlanItemTransitionedEvent(
                Host.Scope,
                Host.InstanceId,
                Host.DefinitionId,
                transition.Trigger,
                transition.Source,
                transition.Destination));
        }

        private Task HandleUnhandledTrigger(PlanItemState state, PlanItemTransition trigger)
        {
            Host.LogWithContext(logger => logger.LogInformation(
                "{ElementType} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | attempted invalid transition {CurrentState} × {StandardEvent} = [INVALID]",
                Host.Definition.GetType().Name,
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                trigger,
                state));

            return Task.CompletedTask;
        }

        protected Task SubscribeToCriteria(
            Expression<Func<Interfaces.Model.PlanItem, IEnumerable<Criterion>>> criteria,
            StreamFlags flags) =>
            Task.WhenAll(criteria.Compile().Invoke(Host.Definition)
                .Select(criterion =>
                {
                    Host.LogWithContext(logger => logger.LogInformation(
                        "{ElementType} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | creating {CriterionType} {CriterionId} subscription to sentry {SentryId}",
                        Host.Definition.GetType().Name,
                        PlanItemDefinition.GetType().Name,
                        Host.Scope,
                        Host.InstanceId,
                        criterion.GetType().Name,
                        criterion.Id,
                        criterion.SentryRef));

                    return Host.SubscribeTo<SentrySatisfiedEvent>(
                        criterion.SentryRef,
                        HandleSentrySatisfied,
                        flags);
                }));

        protected Task UnsubscribeFromCriteria(
            Expression<Func<Interfaces.Model.PlanItem, IEnumerable<Criterion>>> criteria) =>
            Task.WhenAll(criteria.Compile().Invoke(Host.Definition)
                .Select(criterion => Host.UnsubscribeFrom<SentrySatisfiedEvent>(criterion.SentryRef)));

        // 5.24 - PlanItem attributes
        // ~~~~~
        // If a PlanItemControl object is specified for a PlanItem, then it MUST
        // overwrite the PlanItemControl object of the associated
        // PlanItemDefinition element. Otherwise, the buildBehavior of the PlanItem
        // object is specified by the PlanItemControl object of its associated
        // PlanItemDefinition.
        private PlanItemControl GetItemControl() =>
            Host.Definition.ItemControl ?? PlanItemDefinition.DefaultControl;

        // 8.6.2 ManualActivationRule
        // ~~~~~
        // The ManualActivationRule determines whether the Task or Stage instance should move to state Enabled or Active.
        // This rule is evaluated and used when one of the entry criteria of the Task or Stage instance is satisfied. If this rule
        // evaluates to TRUE, the Task or Stage instance transitions from Available to Enabled, otherwise it transitions from
        // Available to Active. This rule impacts Stage or Task instances in Available state.
        protected Task<bool> EvaluateManualActivationRule()
        {
            var itemControl = GetItemControl();
            var rule = itemControl?.ManualActivationRule;
            // 5.5.1 - PlanItemControl attributes and model associations
            // ~~~~~
            // If no ManualActivationRule is specified, then the default is considered TRUE.
            return EvaluateRule<ManualActivationRuleEvaluated>(rule, true);
        }

        // 8.6.3 RequiredRule
        // ~~~~~
        // The RequiredRule determines whether the Milestone, Stage, or Task instance having this condition MUST be in the
        // Completed, Terminated, Failed, or Disabled state in order for its parent Stage instance to transition into the Completed
        // state. This rule MUST be evaluated when the Milestone, Stage, or Task instance is instantiated and transitions to the
        // Available state, and their Boolean value SHOULD be maintained for the rest of the life of the Milestone, Stage, or
        // Task instance. If this rule is not present, then it is considered FALSE. If this rule evaluates to TRUE, the parent Stage
        // instance MUST NOT transition to Complete state unless this Milestone, Stage, or Task instance is in the Completed,
        // Terminated, Failed, or Disabled state.This rule impacts Stage instances in Available state.
        protected async Task<bool> EvaluateRequiredRule()
        {
            var itemControl = GetItemControl();
            var rule = itemControl?.RequiredRule;
            // 5.5.1 - PlanItemControl attributes and model associations
            // ~~~~~
            // If no RequiredRule is specified, the default is FALSE.
            var result = await EvaluateRule<RequiredRuleEvaluated>(rule, false);
            await Host.ConfirmEvents();
            return result;
        }

        // 8.6.4 RepetitionRule
        // ~~~~~
        // This rule MUST be evaluated when the Milestone, Stage, or Task instance is instantiated and transitions to the
        // Available state. The first time a Milestone, Stage, or Task instance is instantiated and transitions to the Available state
        // it is not considered a repetition, nevertheless the RepetitionRule MUST be evaluated and its result discarded.
        // Stage and Task instances with a RepetitionRule will try to create a new instance every time an entry criterion with an
        // OnPart is satisfied.Under that condition the RepetitionRule is re-evaluated and if the Expression evaluated to
        // TRUE, then the new instance is created and because the entry criteria is satisfied it moves from the Available state to
        // either Active or Enabled state depending on the ManualActivationRule.Stage and Task instances with a
        // RepetitionRule that do not have any entry criteria, will try to create a new instance every time an instance transitions
        // into the Complete or Terminate state.Under that condition the RepetitionRule is re-evaluated and if the Expression
        // evaluates to TRUE, a new instance is created.
        //
        // When Tasks, Stages, and Milestones with a RepetitionRole are instantiated the RepetitionRule’s condition is
        // evaluated (during the transition from Create to Available). That first instantiation of the Task, Stage, or Milestone
        // is not considered a repetition and therefore the value of the RepetitionRule’s condition is discarded. After that, every
        // time an entry criterion with an OnPart is satisfied the RepetitionRule’s condition is re-evaluated and if it evaluates to
        // TRUE, a new instance of the Task, Stage, or Milestone is created and transition to Available. This allows users to
        // control the number of repetitions, and under what conditions repetitions should occur.
        protected Task<bool> EvaluateRepetitionRule()
        {
            var itemControl = GetItemControl();
            var rule = itemControl?.RepetitionRule;
            // 5.5.1 - PlanItemControl attributes and model associations
            // ~~~~~
            // If no RepetitionRule object is specified, the default is FALSE.
            return EvaluateRule<RepetitionRuleEvaluated>(rule, false);
        }

        private async Task<bool> EvaluateRule<TEvent>(IExecutableRule rule, bool defaultResult)
            where TEvent : RuleEvaluated<bool>
        {
            ExecutableResult<bool> ruleResult = null;
            if (rule?.Condition != null)
            {
                ruleResult = await Host.GrainFactory.GetGrain<IExpressionGrain>(Host.CaseInstanceId)
                    .ExecuteAsBool(rule.ContextRef, rule.Condition);
            }
            var result = ruleResult?.Value ?? defaultResult;

            var @event = Activator.CreateInstance<TEvent>();
            @event.Result = result;
            @event.Error = ruleResult?.Message;

            Host.RaiseEvent(@event);

            if ((ruleResult?.IsError ?? false) &&
                StateMachine.CanFire(PlanItemTransition.Fault))
            {
                await StateMachine.FireAsync(PlanItemTransition.Fault);
            }

            return result;
        }
    }
}
