using Wayfinder.Grains.Executables;

namespace Wayfinder.Grains.Expressions
{
    // The expression evaluator - an in-process singleton service, NOT a grain.
    //
    // OQ-9 (design 05 §D.2) was measured in #257 and closed to "in-process service":
    //
    //   in-process call                       8-10 ns, flat in context size
    //   today's grain hop                     5.8-6.9 us
    //   bound-request hop, 200 properties     9.1 us
    //   Jint, full production shape           5.8 us
    //
    // The grain hop cost more than the Jint work it wrapped, and the bound-request shape - which
    // §D.2 designed to remove the callback in finding D3 - is cheaper than the old shape only at
    // small contexts, crossing over between 25 and 200 bound properties. Keeping the grain AND
    // adopting the bound request would therefore have made large-context evaluation worse than it
    // was before. So IExpressionGrain and ExpressionGrain are deleted, [StatelessWorker] and the
    // Guid.Empty keying decision in §A.4/§D.2 are moot, and this interface is a plain service
    // contract resolved from DI.
    //
    // Synchronous by design: every input is already bound (see ExpressionRequest), so there is
    // nothing left to await. The async seam lives one layer out, in IExpressionContext, which is
    // where the case-file reads happen.
    public interface IExpressionEvaluator
    {
        ExecutableResult<bool> ExecuteAsBool(ExpressionRequest request);

        ExecutableResult<string> ExecuteAsString(ExpressionRequest request);

        ExecutableResult<Iso8601> ExecuteAsIso8601(ExpressionRequest request);
    }
}
