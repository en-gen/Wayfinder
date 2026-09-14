using System.Threading.Tasks;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Interfaces.Model;

namespace Wayfinder.Grains.Expressions
{
    // The caller-side half of expression evaluation: resolve (bind) the evaluation context, then
    // hand a fully-bound ExpressionRequest to the in-process IExpressionEvaluator.
    //
    // This split is the point of phase P1. The old ExpressionGrain did the binding itself, from
    // inside the evaluator, with a GrainFactory call - which becomes case grain -> evaluator ->
    // case grain once case-file items move into the case grain (P2), i.e. finding D3's distributed
    // wait cycle through a non-reentrant grain. Binding at the caller removes a hop today (the
    // caller reads the case-file item it already has a handle to, instead of asking another grain
    // to read it) and becomes a local field read in P2, which is the entire point.
    public interface IExpressionContext
    {
        // contextRef: optional IDREF to a CaseFileItem (5.4.6.4 IfPart.contextRef, 5.4.11.x rule
        // contextRef, etc.). Null means "not specified" - see ExpressionEvaluator for what that
        // binds (finding I4).
        Task<ExecutableResult<bool>> EvaluateAsBool(string contextRef, Expression expression);

        Task<ExecutableResult<string>> EvaluateAsString(string contextRef, Expression expression);

        Task<ExecutableResult<Iso8601>> EvaluateAsIso8601(string contextRef, Expression expression);
    }
}
