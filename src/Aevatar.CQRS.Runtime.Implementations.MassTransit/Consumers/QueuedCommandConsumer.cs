using Aevatar.CQRS.Runtime.Abstractions.Commands;
using MassTransit;
using System.Text.Json;
using Aevatar.CQRS.Runtime.Abstractions.Dispatch;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Runtime.Implementations.MassTransit.Consumers;

internal sealed class QueuedCommandConsumer : IConsumer<QueuedCommandMessage>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IServiceScopeFactory _scopeFactory;

    public QueuedCommandConsumer(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task Consume(ConsumeContext<QueuedCommandMessage> context)
    {
        var message = context.Message;
        var commandType = Type.GetType(message.CommandType, throwOnError: false)
                          ?? throw new InvalidOperationException(
                              $"Command type '{message.CommandType}' is not resolvable.");

        var command = JsonSerializer.Deserialize(message.PayloadJson, commandType, JsonOptions)
                      ?? throw new InvalidOperationException(
                          $"Unable to deserialize command '{commandType.FullName}'.");

        using var scope = _scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        var handlerType = typeof(ICommandHandler<>).MakeGenericType(commandType);
        var handler = services.GetService(handlerType)
                      ?? throw new InvalidOperationException(
                          $"No command handler registered for '{commandType.FullName}'.");

        var method = handlerType.GetMethod("HandleAsync")
                     ?? throw new InvalidOperationException(
                         $"Handler method missing for '{commandType.FullName}'.");

        var task = method.Invoke(handler, [message.Envelope, command, context.CancellationToken]) as Task
                   ?? throw new InvalidOperationException(
                       $"Invalid handler invocation for '{commandType.FullName}'.");

        await task;
    }
}
