using System.Threading;
using System.Threading.Tasks;

namespace Flow.Application.Mediator
{
    // ADO #32 - the single seam every caller (today's tests, a future Flow.Api controller, a future
    // MCP tool) dispatches through. Transport-agnostic on purpose: nothing here knows about HTTP,
    // Orleans, or any specific host - callers build a command/query DTO and hand it to Send.
    public interface ISender
    {
        Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);

        Task<TResult> Send<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);
    }
}
