using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Flow.Application.Mediator
{
    // ADO #32 - the native ISender implementation.
    // ~~~~~
    // No compile-time handler registry (no MediatR-style generated dispatch table): a command/query's
    // RUNTIME type is closed against ICommandHandler<,>/IQueryHandler<,> via reflection
    // (MakeGenericType), the resulting closed-generic interface is resolved from IServiceProvider
    // (populated by ServiceCollectionExtensions.AddFlowApplication's assembly scan), and HandleAsync
    // is invoked on whatever comes back. Deliberately small and dependency-free per ADO #32's scope
    // fence (no MediatR).
    public sealed class Sender : ISender
    {
        private readonly IServiceProvider _serviceProvider;

        public Sender(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        public Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default)
        {
            if (command is null) throw new ArgumentNullException(nameof(command));

            var handlerType = typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResult));
            return InvokeHandlerAsync<TResult>(handlerType, command, cancellationToken, "command");
        }

        public Task<TResult> Send<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default)
        {
            if (query is null) throw new ArgumentNullException(nameof(query));

            var handlerType = typeof(IQueryHandler<,>).MakeGenericType(query.GetType(), typeof(TResult));
            return InvokeHandlerAsync<TResult>(handlerType, query, cancellationToken, "query");
        }

        private Task<TResult> InvokeHandlerAsync<TResult>(
            Type handlerType, object request, CancellationToken cancellationToken, string requestKind)
        {
            var handler = _serviceProvider.GetService(handlerType);
            if (handler is null)
            {
                throw new InvalidOperationException(
                    $"No handler registered for {requestKind} '{request.GetType().FullName}' " +
                    $"(expected an implementation of {handlerType}). Was AddFlowApplication() called?");
            }

            var handleMethod = handlerType.GetMethod(nameof(ICommandHandler<ICommand<object>, object>.HandleAsync));
            if (handleMethod is null)
            {
                throw new InvalidOperationException($"{handlerType} does not declare a HandleAsync method.");
            }

            return (Task<TResult>)handleMethod.Invoke(handler, new[] { request, cancellationToken });
        }
    }
}
