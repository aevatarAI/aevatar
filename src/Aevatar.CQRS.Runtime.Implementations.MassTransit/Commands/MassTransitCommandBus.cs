using Aevatar.CQRS.Runtime.Abstractions.Commands;
using MassTransit;
using System.Text.Json;

namespace Aevatar.CQRS.Runtime.Implementations.MassTransit.Commands;

internal sealed class MassTransitCommandBus : ICommandBus, ICommandScheduler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IPublishEndpoint _publishEndpoint;

    public MassTransitCommandBus(IPublishEndpoint publishEndpoint)
    {
        _publishEndpoint = publishEndpoint;
    }

    public Task EnqueueAsync<TCommand>(
        CommandEnvelope envelope,
        TCommand command,
        CancellationToken ct = default)
        where TCommand : class
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(command);

        var message = new QueuedCommandMessage(
            envelope,
            ResolveCommandType<TCommand>(),
            JsonSerializer.Serialize(command, JsonOptions));

        return _publishEndpoint.Publish(message, ct);
    }

    public async Task ScheduleAsync<TCommand>(
        CommandEnvelope envelope,
        TCommand command,
        TimeSpan delay,
        CancellationToken ct = default)
        where TCommand : class
    {
        if (delay <= TimeSpan.Zero)
        {
            await EnqueueAsync(envelope, command, ct);
            return;
        }

        await Task.Delay(delay, ct);
        await EnqueueAsync(envelope, command, ct);
    }

    private static string ResolveCommandType<TCommand>()
        where TCommand : class
    {
        var type = typeof(TCommand);
        return type.AssemblyQualifiedName
               ?? type.FullName
               ?? type.Name;
    }
}
