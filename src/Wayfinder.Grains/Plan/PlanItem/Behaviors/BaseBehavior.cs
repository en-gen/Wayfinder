using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Expressions;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Plan.PlanItem.StateMachine;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Wayfinder.Grains.Plan.PlanItem.Behaviors
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

            // TryRepeatOnCompleteOrTerminate is registered per-TRIGGER, not per-state: Table 8.8
            // attaches the no-entry-criteria RepetitionRule re-evaluation to the "complete" and
            // "terminate" transitions specifically, and its "exit" row (exit criteria satisfied,
            // or propagation from an outer Stage terminating) carries no such note - so an
            // Exit-triggered arrival in Terminated must NOT re-evaluate. This matters doubly for
            // parent cascades: a terminating Stage propagates Exit to its children (see
            // HandleParentTransitioned), and respawning a child while its parent shuts down would
            // be exactly backwards.
            StateMachine.Configure(PlanItemState.Completed)
                .OnEntryAsync(HandleEnterTerminal)
                .OnEntryFromAsync(PlanItemTransition.Complete, TryRepeatOnCompleteOrTerminate);

            StateMachine.Configure(PlanItemState.Terminated)
                .OnEntryAsync(HandleEnterTerminal)
                .OnEntryFromAsync(PlanItemTransition.Terminate, TryRepeatOnCompleteOrTerminate);

            StateMachine.OnTransitionedAsync(HandleTransitioned);

            StateMachine.OnUnhandledTriggerAsync(HandleUnhandledTrigger);
        }

        protected virtual Task Define() => Task.CompletedTask;

        // #63: publish is always keyed on the publisher's DEFINITION id (CmmnElementGrain.
        // PublishEvent -> GetCaseEventStream<TEvent>(Definition.Id)), so the subscribe side must
        // key on the parent's definition id too - Host.ParentInstanceId (a freshly-minted-per-
        // child instance id) never matches what the parent actually publishes on, leaving every
        // parent->child cascade (suspend/resume/exit/terminate) dead on the wire. The four
        // HandleParentTransitioned implementations already guard on
        // @event.SourceInstanceId != Host.ParentInstanceId, which only makes sense if this stream
        // carries every instance of the parent DEFINITION and the handler narrows to just this
        // child's actual parent instance by payload - that guard is unchanged by this fix.
        //
        // Root guard: the CasePlanModel root (Host.ParentDefinitionId null/empty, see CaseGrain)
        // has no parent to subscribe to - subscribing on a null/empty key would either throw or
        // create a bogus stream, so skip entirely.
        public virtual Task Activate() =>
            Task.WhenAll(
                SubscribeToCriteria(x => x.EntryCriteria, StreamFlags.Resume),
                SubscribeToCriteria(x => x.ExitCriteria, StreamFlags.Resume),
                string.IsNullOrEmpty(Host.ParentDefinitionId)
                    ? Task.CompletedTask
                    : Host.SubscribeTo<PlanItemTransitionedEvent>(Host.ParentDefinitionId, HandleParentTransitioned, StreamFlags.Create | StreamFlags.Resume),
                Host.State.Defined
                    ? Define()
                    : Task.CompletedTask);

        // virtual: StageBehavior overrides this to gate an externally-invoked manual Complete
        // on Table 8.12's completion criteria (8.6.1) - see its remarks.
        public virtual Task Trigger(PlanItemTransition transition) => StateMachine.FireAsync(transition);

        // protected: CasePlanModelBehavior reuses this for its own Closed-state entry (8.4.1/
        // Table 8.5) - Closed is reached from Completed/Terminated/Failed/Suspended, and only the
        // first two of those already run this cleanup on their own entry, so re-running it on
        // entry to Closed guarantees no dangling ExitCriteria subscription survives into the
        // Case's terminal, immutable state regardless of which prior state it came from.
        protected Task HandleEnterTerminal() => Task.WhenAll(Host.Definition.ExitCriteria
            .Select(c => Host.UnsubscribeFrom<SentrySatisfiedEvent>(c.SentryRef)));

        // 8.6.4 RepetitionRule / 5.4.11.3
        // ~~~~~
        // "Stage and Task instances with a RepetitionRule that do not have any entry criteria,
        // will try to create a new instance every time an instance transitions into the Complete
        // or Terminate state. Under that condition the RepetitionRule is re-evaluated and if the
        // Expression evaluates to TRUE, a new instance is created." Registered as a second entry
        // action alongside HandleEnterTerminal, never folded into it - HandleEnterTerminal also
        // backs CasePlanModelBehavior's Closed entry, which must NOT re-run this repetition check
        // (Closed is not "Complete or Terminate").
        //
        // Scoped to "Stage and Task instances" per the spec text: EventListeners "cannot have
        // RepetitionRule" (5.4.11.3), and a Milestone's no-entry-criteria case is not granted
        // this re-spawn trigger (8.6.4 names only Stage and Task for it) - so the type-check is
        // the spec's own scoping, not defensive noise. The outermost CasePlanModel Stage is
        // excluded: it implements the CASE lifecycle (8.4.1), whose Table 8.6 defines no
        // repetition semantics, and it has no parent Stage listening for a repeat to instantiate.
        //
        // Publish-before-Repeated ordering matches the entry-criterion repetition path
        // (StageBehavior/TaskBehavior.HandleSentrySatisfied). The trailing ConfirmEvents() is
        // Bug #61 discipline: this method runs as a state ENTRY action, i.e. AFTER
        // HandleTransitioned already raised-and-confirmed the Transitioned event (Stateless
        // invokes the transition callback before the destination state's entry actions), so the
        // RepetitionRuleEvaluated/Repeated events raised here have no later confirm to ride on
        // and would otherwise sit queued in TentativeState indefinitely.
        private async Task TryRepeatOnCompleteOrTerminate()
        {
            if (!(PlanItemDefinition is Stage || PlanItemDefinition is BaseTask)) return;
            if (PlanItemDefinition is Stage { IsCasePlanModel: true }) return;
            if (Host.Definition.EntryCriteria.Any()) return;
            if (GetItemControl()?.RepetitionRule == null) return;

            if (await EvaluateRepetitionRule())
            {
                await Host.Publish(new PlanItemRepetitionCriteriaMetEvent(
                    Host.Scope,
                    Host.InstanceId,
                    Host.DefinitionId,
                    Host.State.Repetition));

                Host.RaiseEvent(new Repeated());
            }

            await Host.ConfirmEvents();
        }

        private async Task HandleTransitioned(PlanItemStateMachine.Transition transition)
        {
            Host.LogWithContext(logger => logger.LogInformation(
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | processed transition {PreviousState} × {StandardEvent} = {CurrentState}",
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

            // D10 - PlanItemStateMachine registers PlanItemTransition.Exit as a Stateless
            // parameterized trigger (SetTriggerParameters<string>) so StageBehavior/TaskBehavior's
            // ExitCriterion branch can carry "which ExitCriterion drove this Exit" alongside the
            // trigger itself (see PlanItemStateMachine.FireAsync(PlanItemTransition, string)).
            // Stateless surfaces that payload here via Transition.Parameters (never null - an empty
            // array for a parameterless-fired trigger, e.g. an Exit cascaded from
            // HandleParentTransitioned rather than driven by this PlanItem's own exit criterion) -
            // exactly the piece SentryGrain.HandlePlanItemTransitioned's exitCriterionRef match has
            // been unable to reach until now.
            var exitCriterionRef = transition.Trigger == PlanItemTransition.Exit && transition.Parameters.Length > 0
                ? transition.Parameters[0] as string
                : null;

            await Host.Publish(new PlanItemTransitionedEvent(
                Host.Scope,
                Host.InstanceId,
                Host.DefinitionId,
                transition.Trigger,
                transition.Source,
                transition.Destination,
                exitCriterionRef));
        }

        private Task HandleUnhandledTrigger(PlanItemState state, PlanItemTransition trigger)
        {
            Host.LogWithContext(logger => logger.LogInformation(
                "{ElementType} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | attempted invalid transition {CurrentState} × {StandardEvent} = [INVALID]",
                "PlanItem",
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                trigger,
                state));

            return Task.CompletedTask;
        }

        protected Task SubscribeToCriteria(
            Expression<Func<IBehaviorDefinition, IEnumerable<Criterion>>> criteria,
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
            Expression<Func<IBehaviorDefinition, IEnumerable<Criterion>>> criteria) =>
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
        //
        // discard: true only for the FIRST evaluation (HandleEnterAvailableFromCreate, on the
        // Create -> Available transition) - the rule is still evaluated (so a malformed
        // expression's error is still recorded on the RepetitionRuleEvaluated audit event, same as
        // any other evaluation - see EvaluateRule below) and a RepetitionRuleEvaluated is still
        // raised for audit purposes, but flagged so PlanItemStore/CaseStore do not let its Result
        // update the persisted Repeatable flag, and the boolean this method returns MUST NOT be
        // acted on by the discarding caller. Every other call site (entry-criterion OnPart
        // satisfied; the no-entry-criteria Complete/Terminate re-evaluation above) is a real,
        // actionable evaluation and leaves this false.
        protected Task<bool> EvaluateRepetitionRule(bool discard = false)
        {
            var itemControl = GetItemControl();
            var rule = itemControl?.RepetitionRule;
            // 5.5.1 - PlanItemControl attributes and model associations
            // ~~~~~
            // If no RepetitionRule object is specified, the default is FALSE.
            return EvaluateRule<RepetitionRuleEvaluated>(rule, false, @event => @event.Discard = discard);
        }

        // #158 - a rule with no configured Condition never produces an ExecutableResult
        // (ruleResult stays null), so ValueOr can't help there - that case falls back to
        // defaultResult via the `??` below. Once a Condition DID run, ValueOr(defaultResult) is the
        // one place that decides error-vs-value (see ExecutableResult.ValueOr's remarks: this is
        // the same shape of coalesce that produced #158 the first time, on Value instead of via
        // IsError, so callers should route through it rather than hand-rolling `?.Value ?? x`
        // again). Compare SentryGrain.EvaluateIfPart, which deliberately does NOT use a
        // caller-supplied default on error - a Sentry's IfPart has no spec-mandated fallback, so it
        // fails CLOSED (does not fire) instead.
        private async Task<bool> EvaluateRule<TEvent>(IExecutableRule rule, bool defaultResult, Action<TEvent> configureEvent = null)
            where TEvent : RuleEvaluated<bool>
        {
            ExecutableResult<bool> ruleResult = null;
            if (rule?.Condition != null)
            {
                ruleResult = await Host.GrainFactory.GetGrain<IExpressionGrain>(Host.CaseInstanceId)
                    .ExecuteAsBool(rule.ContextRef, rule.Condition);
            }
            var result = ruleResult?.ValueOr(defaultResult) ?? defaultResult;

            var @event = Activator.CreateInstance<TEvent>();
            @event.Result = result;
            @event.Error = ruleResult?.Message;
            configureEvent?.Invoke(@event);

            Host.RaiseEvent(@event);

            // #158 - rules evaluate while this item is Available, where PlanItemTransition.Fault is
            // not a permitted transition (see PlanItemStateMachine.ConfigureForStageOrTask), so
            // CanFire(Fault) is false at every real call site today and this is a no-op: an erroring
            // expression's Message lands only on the RuleEvaluated audit event raised above, never
            // as a state transition. Left as-is deliberately - making Fault reachable from Available
            // is a state-machine semantics change, out of scope here.
            if ((ruleResult?.IsError ?? false) &&
                StateMachine.CanFire(PlanItemTransition.Fault))
            {
                await StateMachine.FireAsync(PlanItemTransition.Fault);
            }

            return result;
        }
    }
}
