using System;
using System.Threading;
using System.Threading.Tasks;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Interfaces.Model;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;

namespace Wayfinder.Grains.Expressions
{
    // D1 - Expression context binder
    // ~~~~~
    // Before this work item, every caller below (SentryGrain.EvaluateIfPart, BaseBehavior.EvaluateRule
    // for ManualActivationRule/RequiredRule/RepetitionRule, PlanningTableGrain.EvaluateApplicabilityRule)
    // already called ExecuteAsBool(rule.ContextRef, rule.Condition) - the call graph for 8.6's Behavior
    // Property Rules and 8.5's Sentry IfPart was already wired end to end. What was missing was entirely
    // inside BuildExecutable below: it built a bare Executable over the expression string and returned it
    // without ever binding the referenced CaseFileItem's data, so `amount > 100` had no `amount` to see.
    // This grain is therefore the single chokepoint for both deviations this work item is scoped to
    // (Sentry IfPart) and the behavior-rule callers that already existed - fixing it here unlocks all of
    // them without touching any of those call sites. Behavior-rule *wiring* itself is out of scope for
    // this work item (that already shipped); only this binder was the gap.
    [StatelessWorker]
    public class ExpressionGrain : Grain, IExpressionGrain
    {
        // 5.4.6.4 / 5.3.2 - IfPart.ContextRef / ApplicabilityRule.ContextRef etc. are all optional
        // IDREFs to a CaseFileItem. When bound, the CaseFileItem's Value (a JsonNode of any shape -
        // object, array, or scalar; see CaseFileItemStore.Value) is exposed to the expression under
        // two names:
        //   - "value" - the spec-agnostic, context-shape-independent alias. Matches this codebase's
        //     own naming for the field everywhere else (CaseFileItemSnapshot.Value,
        //     ICaseFileItemGrain.Update(JsonNode value), etc.) and lets a condition be written
        //     without needing to know or repeat the contextRef's id, e.g. `value.amount > 100`.
        //   - the contextRef's own id (e.g. `TheCaseFileItem.amount > 100`) - the spec-natural
        //     choice, since that identifier is exactly the name a modeler used to declare the
        //     IfPart/rule's context in the model.
        // Both are bound to the identical value; a condition may use either without needing both to
        // be documented per-model. This is a naming *convention* of this engine, not something 5.4.7
        // (Expressions) prescribes - the spec only says an Expression "operates over Properties and
        // CaseFileItems in the CaseFile," not what identifier a bound CaseFileItem is exposed under.
        private const string ValueArgumentName = "value";

        private Guid _caseInstanceId;
        private readonly Func<string, IExecutable> _executable;

        private readonly ILogger _logger;

        public ExpressionGrain(
            Func<string, IExecutable> executable,
            ILogger<ExpressionGrain> logger)
        {
            _executable = executable ?? throw new ArgumentNullException(nameof(executable));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _caseInstanceId = this.GetPrimaryKey();
            return base.OnActivateAsync(cancellationToken);
        }

        public async Task<ExecutableResult<bool>> ExecuteAsBool(string contextRef, Expression expression)
        {
            var built = await BuildExecutable(contextRef, expression);
            return built.IsError
                ? ExecutableResult<bool>.Failure(built.Message)
                : built.Executable.ExecuteAsBool();
        }

        public async Task<ExecutableResult<string>> ExecuteAsString(string contextRef, Expression expression)
        {
            var built = await BuildExecutable(contextRef, expression);
            return built.IsError
                ? ExecutableResult<string>.Failure(built.Message)
                : built.Executable.ExecuteAsString();
        }

        public async Task<ExecutableResult<Iso8601>> ExecuteAsIso8601(string contextRef, Expression expression)
        {
            var built = await BuildExecutable(contextRef, expression);
            if (built.IsError)
            {
                return ExecutableResult<Iso8601>.Failure(built.Message);
            }

            var result = built.Executable.ExecuteAsString();

            if (result.IsError)
            {
                return ExecutableResult<Iso8601>.Failure(result.Message);
            }

            try
            {
                var iso = new Iso8601(result.Value);

                return ExecutableResult<Iso8601>.Success(iso);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "failed to parse expression result {ExpressionResult} to ISO8601", result.Value);
                return ExecutableResult<Iso8601>.Failure(ex.Message);
            }
        }

        // Returns either a bound-and-ready IExecutable, or an error message when contextRef names a
        // CaseFileItem that was never created (see remarks below) - the caller folds either shape
        // into its own ExecutableResult<T>.Failure so a missing context degrades exactly like an
        // expression that threw, never as an unhandled exception.
        private async Task<(bool IsError, string Message, IExecutable Executable)> BuildExecutable(string contextRef, Expression expression)
        {
            var executor = _executable(expression.Body);

            if (contextRef == null)
            {
                return (false, null, executor);
            }

            var snapshot = await GrainFactory.GetCaseFileItem(_caseInstanceId, contextRef).GetSnapshot();

            // 8.3 Table 8.1/8.2 has no state for "never created" - a CaseFileItem instance either
            // exists (Available/Discarded) or its grain has simply never been activated by a Create
            // call. CmmnElementGrain leaves Definition null in that case (see CaseFileItemStore /
            // CmmnElementGrain.Definition), which is the only reliable signal available here that
            // contextRef refers to a CaseFileItem that does not (yet) exist for this case instance.
            //
            // Deliberate simplification, documented per this work item's brief: this engine treats
            // that as an expression failure (IfPart NOT satisfied, logged) rather than binding `null`
            // and letting a member-access expression like `value.amount` throw its own TypeError
            // inside Jint. Both paths converge on the same outcome (Failure -> sentry does not fire),
            // but detecting it here up front produces an actionable log message ("context CaseFileItem
            // X was never created for this case") instead of a generic Jint error message that would
            // otherwise be indistinguishable from a genuinely broken expression.
            if (snapshot.Definition == null)
            {
                var message = $"context CaseFileItem '{contextRef}' has not been created for case {_caseInstanceId}";
                _logger.LogWarning(
                    "unable to evaluate expression: {Expression} → {ErrorMessage}",
                    expression.Body,
                    message);
                return (true, message, null);
            }

            executor
                .WithJsonArgument(ValueArgumentName, snapshot.Value)
                .WithJsonArgument(contextRef, snapshot.Value);

            return (false, null, executor);
        }
    }
}
