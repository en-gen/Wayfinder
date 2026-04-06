using System.Threading.Tasks;
using Flow.Grains.Executables;
using Flow.Grains.Interfaces.Model;
using Orleans;

namespace Flow.Grains.Expressions
{
    public interface IExpressionGrain : IGrainWithGuidKey
    {
        Task<ExecutableResult<bool>> ExecuteAsBool(/*CaseContext context, */string contextRef, Expression expression);

        Task<ExecutableResult<string>> ExecuteAsString(/*CaseContext context, */string contextRef, Expression expression);

        Task<ExecutableResult<Iso8601>> ExecuteAsIso8601( /*CaseContext context, */ string contextRef, Expression expression);
    }
}
