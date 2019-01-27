using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Expressions;
using Flow.Grains.Interfaces.Model;
using Orleans.Streams;
using Xunit;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    public abstract class BaseBehavior<TPlanItemDefinition> : IPlanItemBehavior
        where TPlanItemDefinition : PlanItemDefinition
    {
        protected IBehaviorHost Host { get; }
        protected TPlanItemDefinition PlanItemDefinition { get; }

        protected BaseBehavior(IBehaviorHost host, TPlanItemDefinition planItemDefinition)
        {
            Host = host;
            PlanItemDefinition = planItemDefinition;
        }

        public abstract Task Define();
        public abstract Task Activate();
        public abstract Task<bool> IsUserCompletable();
        protected abstract Task HandleSentrySatisfied(SentrySatisfiedEvent @event, StreamSequenceToken token = null);
        
        // 5.24 - PlanItem attributes
        // ~~~~~
        // If a PlanItemControl object is specified for a PlanItem, then it MUST
        // overwrite the PlanItemControl object of the associated
        // PlanItemDefinition element. Otherwise, the buildBehavior of the PlanItem
        // object is specified by the PlanItemControl object of its associated
        // PlanItemDefinition.
        private PlanItemControl GetItemControl() => Host.Definition.ItemControl ?? PlanItemDefinition.DefaultControl;

        // 8.6.2 ManualActivationRule
        // ~~~~~
        // The ManualActivationRule determines whether the Task or Stage instance should move to state Enabled or Active.
        // This rule is evaluated and used when one of the entry criteria of the Task or Stage instance is satisfied. If this rule
        // evaluates to TRUE, the Task or Stage instance transitions from Available to Enabled, otherwise it transitions from
        // Available to Active. This rule impacts Stage or Task instances in Available state.
        protected async Task<bool> EvaluateManualActivationRule()
        {
            var result = true;

            var itemControl = GetItemControl();
            var rule = itemControl?.ManualActivationRule;
            // 5.5.1 - PlanItemControl attributes and model associations
            // ~~~~~
            // If no ManualActivationRule is specified, then the default is considered TRUE.
            if (rule?.Condition != null)
            {
                var ruleResult = await EvaluateBooleanExpression(rule.ContextRef, rule.Condition);
                result = ruleResult ?? true;
            }

            Host.RaiseEvent(new ManualActivationRuleEvaluated
            {
                Result = result
            });

            return result;
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
        protected async Task EvaluateRequiredRule()
        {
            var result = false;

            var itemControl = GetItemControl();
            var rule = itemControl?.RequiredRule;
            // 5.5.1 - PlanItemControl attributes and model associations
            // ~~~~~
            // If no RequiredRule is specified, the default is FALSE.
            if (rule?.Condition != null)
            {
                var ruleResult = await EvaluateBooleanExpression(rule.ContextRef, rule.Condition);
                if (ruleResult.HasValue)
                {
                    result = ruleResult.Value;
                }
            }

            Host.RaiseEvent(new RequiredRuleEvaluated
            {
                Result = result
            });
            await Host.ConfirmEvents();
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
        protected async Task<bool> EvaluateRepetitionRule()
        {
            var result = false;

            var itemControl = GetItemControl();
            var rule = itemControl?.RepetitionRule;
            // 5.5.1 - PlanItemControl attributes and model associations
            // ~~~~~
            // If no RepetitionRule object is specified, the default is FALSE.
            if (rule?.Condition != null)
            {
                var ruleResult = await EvaluateBooleanExpression(rule.ContextRef, rule.Condition);
                result = ruleResult ?? false;
            }

            Host.RaiseEvent(new RepetitionRuleEvaluated
            {
                Result = result
            });

            return result;
        }

        private async Task<bool?> EvaluateBooleanExpression(string contextRef, Expression expression)
        {
            // TODO: resolve CaseContext
            var result = await Host.GrainFactory.GetGrain<IExpressionGrain>(Host.CaseInstanceId)
                .ExecuteAsBool(contextRef, expression);

            if (result.IsError)
            {
                if (Host.StateMachine.CanFire(PlanItemTransition.Fault))
                {
                    // TODO: set fault error msg from result in state?
                    await Host.StateMachine.FireAsync(PlanItemTransition.Fault);
                }

                return null;
            }

            return result.Value;
        }
    }
}
