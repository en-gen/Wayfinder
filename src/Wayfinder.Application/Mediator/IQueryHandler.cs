using System.Threading;
using System.Threading.Tasks;

namespace Wayfinder.Application.Mediator
{
    // ADO #32 - the read-side counterpart to ICommandHandler<,>; see that file's remarks.
    public interface IQueryHandler<TQuery, TResult>
        where TQuery : IQuery<TResult>
    {
        Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
    }
}
