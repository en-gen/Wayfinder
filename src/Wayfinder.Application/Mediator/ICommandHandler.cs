using System.Threading;
using System.Threading.Tasks;

namespace Wayfinder.Application.Mediator
{
    // ADO #32 - handles exactly one command type. TCommand's own ICommand<TResult> constraint is
    // what lets Sender go from a command's runtime type straight to the one handler interface that
    // can process it (typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResult))),
    // with no separate registry to keep in sync.
    public interface ICommandHandler<TCommand, TResult>
        where TCommand : ICommand<TResult>
    {
        Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
    }
}
