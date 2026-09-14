using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Wayfinder.Grains.Executables;
using Microsoft.Extensions.Logging;

namespace Wayfinder.Grains.Expressions
{
    // I-4 (no callbacks). THIS TYPE MUST NEVER TAKE A DEPENDENCY ON Orleans.IGrainFactory OR
    // IClusterClient - not a constructor parameter, not a field, not a base class. That edge is
    // finding D3's distributed wait cycle: the old ExpressionGrain.BuildExecutable fetched the
    // context item itself (GrainFactory.GetCaseFileItem(...).GetSnapshot()), which once case-file
    // items live inside the case grain (phase P2) becomes case grain -> evaluator -> case grain.
    // The case grain is non-reentrant, so the inner call queues behind the outer turn and both
    // block until the response timeout.
    //
    // If you are here because an expression needs data it cannot see, bind it at the CALLER (see
    // ExpressionContext) and put it on the ExpressionRequest. Do not "just fetch it here".
    // ExpressionEvaluatorArchitectureTests exists to stop exactly that.
    public sealed class ExpressionEvaluator : IExpressionEvaluator
    {
        // 5.4.6.4 / 5.3.2 - IfPart.ContextRef / ApplicabilityRule.ContextRef etc. are all optional
        // IDREFs to a CaseFileItem. When bound, the CaseFileItem's Value (a JsonNode of any shape -
        // object, array, or scalar; see CaseFileItemStore.Value) is exposed to the expression under
        // two names:
        //   - "value" - the spec-agnostic, context-shape-independent alias. Matches this codebase's
        //     own naming for the field everywhere else (CaseFileItemSnapshot.Value,
        //     ICaseFileItemGrain.Update(JsonNode value), etc.) and lets a condition be written
        //     without needing to know or repeat the contextRef's id, e.g. value.amount > 100.
        //   - the contextRef's own id (e.g. TheCaseFileItem.amount > 100) - the spec-natural
        //     choice, since that identifier is exactly the name a modeler used to declare the
        //     IfPart/rule's context in the model.
        // Both are bound to the identical value; a condition may use either without needing both to
        // be documented per-model. This is a naming *convention* of this engine, not something 5.4.7
        // (Expressions) prescribes - the spec only says an Expression "operates over Properties and
        // CaseFileItems in the CaseFile," not what identifier a bound CaseFileItem is exposed under.
        public const string ValueArgumentName = "value";

        private readonly Func<string, IExecutable> _executable;
        private readonly ILogger _logger;

        public ExpressionEvaluator(
            Func<string, IExecutable> executable,
            ILogger<ExpressionEvaluator> logger)
        {
            _executable = executable ?? throw new ArgumentNullException(nameof(executable));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public ExecutableResult<bool> ExecuteAsBool(ExpressionRequest request)
        {
            var built = Bind(request);
            return built.IsError
                ? ExecutableResult<bool>.Failure(built.Message)
                : built.Executable.ExecuteAsBool();
        }

        public ExecutableResult<string> ExecuteAsString(ExpressionRequest request)
        {
            var built = Bind(request);
            return built.IsError
                ? ExecutableResult<string>.Failure(built.Message)
                : built.Executable.ExecuteAsString();
        }

        public ExecutableResult<Iso8601> ExecuteAsIso8601(ExpressionRequest request)
        {
            var built = Bind(request);
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
        private (bool IsError, string Message, IExecutable Executable) Bind(ExpressionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var executor = _executable(request.Expression.Body);

            if (request.ContextRef == null)
            {
                // I4 - Table 5.32 IfPart.contextRef / Table 5.54 RepetitionRule.contextRef: "The
                // caseFileItem that serves as starting point for evaluation of the Expression ...
                // If not specified, evaluation starts at the CaseFile object that is referenced by
                // the Case as its caseFileModel." Before this change a null contextRef produced a
                // BARE executor with no bindings at all, so a condition over a case-file item threw
                // a Jint ReferenceError, the IfPart faulted and the sentry could never satisfy.
                // That was a deviation, not a simplification.
                //
                // Binding by name is an engine convention, not XPath: "evaluation starts at" is
                // XPath language and JavaScript has no context node (the same admission the
                // contextRef binding below already carries; real XPath support, finding I2, remains
                // unimplemented and orthogonal). The caseFileModel object is exposed under "value"
                // AND each item under its own definition id, so the SAME syntax works whether
                // contextRef names ItemA or is omitted - and a multi-item condition such as
                // ItemA.amount > ItemB.limit becomes expressible, which closes I4's related note.
                BindCaseFileModel(executor, request.CaseFileModel);
                return (false, null, executor);
            }

            // 8.3 Table 8.1/8.2 has no state for "never created" - a CaseFileItem instance either
            // exists (Available/Discarded) or its grain has simply never been activated by a Create
            // call. The caller detects that (see ExpressionContext.Bind) and reports it here as
            // ContextMissing, which is the only reliable signal available that contextRef refers to
            // a CaseFileItem that does not (yet) exist for this case instance.
            //
            // Deliberate, and deliberately ASYMMETRIC with the contextRef-absent branch above: an
            // explicit contextRef naming a nonexistent item is treated as an expression failure
            // (IfPart NOT satisfied, logged) rather than binding null and letting a member-access
            // expression like value.amount throw its own TypeError inside Jint. Both paths converge
            // on the same outcome (Failure -> sentry does not fire), but detecting it up front
            // produces an actionable message instead of a generic Jint error indistinguishable from
            // a genuinely broken expression. With contextRef ABSENT, by contrast, a reference to a
            // non-existent item simply yields undefined and the expression's own truthiness rules
            // apply - because the caseFileModel binding legitimately contains only the items that
            // exist (case-file items are created ad hoc, #16, not enumerated from the model),
            // whereas an explicit contextRef that resolves to nothing is a model/data error worth
            // surfacing. The inconsistency is stated rather than hidden (design 05 §D.2).
            if (request.ContextMissing)
            {
                var message = $"context CaseFileItem '{request.ContextRef}' has not been created for case {request.CaseInstanceId}";
                _logger.LogWarning(
                    "unable to evaluate expression: {Expression} → {ErrorMessage}",
                    request.Expression.Body,
                    message);
                return (true, message, null);
            }

            executor
                .WithJsonArgument(ValueArgumentName, request.ContextValue)
                .WithJsonArgument(request.ContextRef, request.ContextValue);

            return (false, null, executor);
        }

        private static void BindCaseFileModel(IExecutable executor, IReadOnlyDictionary<string, JsonNode> caseFileModel)
        {
            if (caseFileModel == null || caseFileModel.Count == 0)
            {
                // No case-file items exist yet. Binding nothing is the honest representation of an
                // empty CaseFile, and it preserves the previous behaviour exactly for every
                // expression that never referenced the case file in the first place - notably
                // TimerEventListenerBehavior's timerExpression, which passes a null contextRef
                // because a TimerExpression has no contextRef attribute at all.
                return;
            }

            // One JsonObject is assembled for the "value" alias. DeepClone because a JsonNode may
            // have only one parent: the per-item bindings below reuse the caller's original nodes,
            // so the aggregate must own copies rather than re-parent them out from under the caller.
            var aggregate = new JsonObject();

            foreach (var item in caseFileModel)
            {
                aggregate[item.Key] = item.Value?.DeepClone();
                executor.WithJsonArgument(item.Key, item.Value);
            }

            executor.WithJsonArgument(ValueArgumentName, aggregate);
        }
    }
}
