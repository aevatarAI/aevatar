using Aevatar.CQRS.Runtime.Abstractions.Commands;
using Aevatar.CQRS.Runtime.Abstractions.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Wolverine.Attributes;

namespace Aevatar.CQRS.Runtime.Implementations.Wolverine.Handlers;

[LocalQueue("cqrs-commands")]
public sealed class WolverineQueuedCommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [WolverineHandler]
    public static async Task Handle(
        QueuedCommandMessage message,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var commandType = Type.GetType(message.CommandType, throwOnError: false)
                          ?? throw new InvalidOperationException(
                              $"Command type '{message.CommandType}' is not resolvable.");

        var command = JsonSerializer.Deserialize(message.PayloadJson, commandType, JsonOptions)
                      ?? throw new InvalidOperationException(
                          $"Unable to deserialize command '{commandType.FullName}'.");

        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        var handlerType = typeof(ICommandHandler<>).MakeGenericType(commandType);
        var handler = services.GetService(handlerType)
                      ?? throw new InvalidOperationException(
                          $"No command handler registered for '{commandType.FullName}'.");

        var method = handlerType.GetMethod("HandleAsync")
                     ?? throw new InvalidOperationException(
                         $"Handler method missing for '{commandType.FullName}'.");

        var task = method.Invoke(handler, [message.Envelope, command, ct]) as Task
                   ?? throw new InvalidOperationException(
                       $"Invalid handler invocation for '{commandType.FullName}'.");

        await task;
    }
}
