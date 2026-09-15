using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan;
using Orleans;

namespace Wayfinder.Grains.Expressions
{
    // Binds an expression's evaluation context, then evaluates in-process. One instance per grain
    // activation; it holds no state beyond the handles it was constructed with.
    //
    // On today's topology case-file items are still separate grains, so binding means a
    // GetSnapshot() from here. That is the SAME read the old ExpressionGrain performed, moved one
    // hop earlier: it used to cost caller -> ExpressionGrain -> CaseFileItemGrain, and now costs
    // caller -> CaseFileItemGrain. When P2 moves case-file items into the case grain, every read
    // below becomes a local field read.
    public sealed class ExpressionContext : IExpressionContext
    {
        private readonly IExpressionEvaluator _evaluator;
        private readonly IGrainFactory _grainFactory;
        private readonly Guid _caseInstanceId;
        private readonly Func<CaseModelPin> _pin;

        public ExpressionContext(
            IExpressionEvaluator evaluator,
            IGrainFactory grainFactory,
            Guid caseInstanceId,
            Func<CaseModelPin> pin)
        {
            _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
            _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
            _caseInstanceId = caseInstanceId;
            _pin = pin ?? throw new ArgumentNullException(nameof(pin));
        }

        public async Task<ExecutableResult<bool>> EvaluateAsBool(string contextRef, Expression expression) =>
            _evaluator.ExecuteAsBool(await Bind(contextRef, expression));

        public async Task<ExecutableResult<string>> EvaluateAsString(string contextRef, Expression expression) =>
            _evaluator.ExecuteAsString(await Bind(contextRef, expression));

        public async Task<ExecutableResult<Iso8601>> EvaluateAsIso8601(string contextRef, Expression expression) =>
            _evaluator.ExecuteAsIso8601(await Bind(contextRef, expression));

        private async Task<ExpressionRequest> Bind(string contextRef, Expression expression)
        {
            if (contextRef == null)
            {
                return new ExpressionRequest
                {
                    Expression = expression,
                    CaseInstanceId = _caseInstanceId,
                    CaseFileModel = await BindCaseFileModel()
                };
            }

            var snapshot = await _grainFactory.GetCaseFileItem(_caseInstanceId, contextRef).GetSnapshot();

            // 8.3 Table 8.1/8.2 has no state for "never created" - a CaseFileItem instance either
            // exists (Available/Discarded) or its grain has simply never been activated by a Create
            // call. CmmnElementGrain leaves Definition null in that case (see CaseFileItemStore /
            // CmmnElementGrain.Definition), which is the only reliable signal available here that
            // contextRef refers to a CaseFileItem that does not (yet) exist for this case instance.
            var missing = snapshot.Definition == null;

            return new ExpressionRequest
            {
                Expression = expression,
                CaseInstanceId = _caseInstanceId,
                ContextRef = contextRef,
                ContextValue = missing ? null : snapshot.Value,
                ContextMissing = missing
            };
        }

        // I4 - "If not specified, evaluation starts at the CaseFile object that is referenced by
        // the Case as its caseFileModel" (Table 5.32 IfPart.contextRef, Table 5.54
        // RepetitionRule.contextRef).
        //
        // The candidate ids come from the pinned model (CaseModelPin.CaseFileItemIds); which of
        // them actually exist is a runtime question, so each is read and the ones that were never
        // created are simply left out. That is what makes the contextRef-absent asymmetry
        // documented in ExpressionEvaluator.Bind correct rather than sloppy: this binding
        // legitimately contains only the items that exist, because case-file items are created ad
        // hoc (#16) and are not enumerated into existence from the model.
        private async Task<IReadOnlyDictionary<string, JsonNode>> BindCaseFileModel()
        {
            var ids = _pin()?.CaseFileItemIds;

            // No pinned model (an element defined outside a case Create flow) or a case with no
            // declared caseFileModel binds nothing, which is exactly the pre-I4 behaviour.
            if (ids == null || ids.Length == 0) return null;

            var snapshots = await Task.WhenAll(ids
                .Select(async id => (id, snapshot: await _grainFactory.GetCaseFileItem(_caseInstanceId, id).GetSnapshot())));

            var bound = new Dictionary<string, JsonNode>();

            foreach (var (id, snapshot) in snapshots)
            {
                if (snapshot.Definition == null) continue;
                bound[id] = snapshot.Value;
            }

            return bound;
        }
    }
}
