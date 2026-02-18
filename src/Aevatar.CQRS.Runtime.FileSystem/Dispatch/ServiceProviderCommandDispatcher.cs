using Aevatar.CQRS.Runtime.Abstractions.Commands;
using Aevatar.CQRS.Runtime.Abstractions.Dispatch;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Runtime.FileSystem.Dispatch;

internal sealed class ServiceProviderCommandDispatcher : ICommandDispatcher
{
    private readonly IServiceProvider _services;

    public ServiceProviderCommandDispatcher(IServiceProvider services)
    {
        _services = services;
    }

    public async Task DispatchAsync(CommandEnvelope envelope, object command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var commandType = command.GetType();
        var handlerType = typeof(ICommandHandler<>).MakeGenericType(commandType);
        var handler = _services.GetService(handlerType);
        if (handler == null)
            throw new InvalidOperationException($"No command handler registered for '{commandType.FullName}'.");

        var method = handlerType.GetMethod("HandleAsync")
                     ?? throw new InvalidOperationException($"Handler method missing for '{commandType.FullName}'.");

        var task = method.Invoke(handler, [envelope, command, ct]) as Task;
        if (task == null)
            throw new InvalidOperationException($"Invalid handler invocation for '{commandType.FullName}'.");

        await task;
    }
}
