namespace Wayfinder.Application.Mediator
{
    // ADO #32 - the native mediator's write-side marker.
    // ~~~~~
    // A command is an intent to change state. It carries no behavior itself - it is dispatched
    // through ISender.Send and handled by exactly one ICommandHandler<TCommand, TResult> found by
    // convention (the closed generic interface implemented for the command's own runtime type; see
    // Sender). Deliberately just a marker: no MediatR IRequest, no third-party base type - this
    // interface and IQuery<TResult> are the entire compile-time contract the mediator understands.
    public interface ICommand<TResult>
    {
    }
}
