using Aevatar.CQRS.Runtime.Abstractions.Commands;
using Aevatar.CQRS.Runtime.Abstractions.Serialization;
using MassTransit;

namespace Aevatar.CQRS.Runtime.Implementations.MassTransit.Commands;

internal sealed class MassTransitCommandBus : ICommandBus, ICommandScheduler
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ICommandPayloadSerializer _serializer;

    public MassTransitCommandBus(
        IPublishEndpoint publishEndpoint,
        ICommandPayloadSerializer serializer)
    {
        _publishEndpoint = publishEndpoint;
        _serializer = serializer;
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
            _serializer.Serialize(command));

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
