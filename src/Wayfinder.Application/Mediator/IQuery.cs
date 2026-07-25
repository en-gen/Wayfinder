namespace Wayfinder.Application.Mediator
{
    // ADO #32 - the native mediator's read-side marker.
    // ~~~~~
    // A query is a read-only intent - it must not change state. Otherwise identical in shape and
    // dispatch mechanics to ICommand<TResult>: kept as a distinct interface (rather than reusing
    // ICommand) so a handler, a DI registration, and a future transport layer can all tell reads
    // and writes apart at a glance and, if useful, dispatch them differently in the future.
    public interface IQuery<TResult>
    {
    }
}
