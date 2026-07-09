using System.Threading.Tasks;
using Flow.Grains.Executables;
using Flow.Grains.Interfaces.Model;
using Orleans;

namespace Flow.Grains.Expressions
{
    // Addressed by case instance id (IGrainWithGuidKey - see ExpressionGrain.OnActivateAsync), so
    // contextRef only ever needs to resolve a CaseFileItem within that one case instance; no
    // separate case-context parameter is needed on these calls.
    public interface IExpressionGrain : IGrainWithGuidKey
    {
        // contextRef: optional IDREF to a CaseFileItem (5.4.6.4 IfPart.contextRef, 5.4.11.x rule
        // contextRef, etc.) - see ExpressionGrain.BuildExecutable for how it is resolved and bound.
        Task<ExecutableResult<bool>> ExecuteAsBool(string contextRef, Expression expression);

        Task<ExecutableResult<string>> ExecuteAsString(string contextRef, Expression expression);

        Task<ExecutableResult<Iso8601>> ExecuteAsIso8601(string contextRef, Expression expression);
    }
}
