using Aevatar.CQRS.Runtime.Abstractions.Commands;
using Aevatar.CQRS.Runtime.Abstractions.Serialization;
using Wolverine;
using AevatarCommandBus = Aevatar.CQRS.Runtime.Abstractions.Commands.ICommandBus;

namespace Aevatar.CQRS.Runtime.Implementations.Wolverine.Commands;

internal sealed class WolverineCommandBus : AevatarCommandBus, ICommandScheduler
{
    private readonly IMessageBus _bus;
    private readonly ICommandPayloadSerializer _serializer;

    public WolverineCommandBus(
        IMessageBus bus,
        ICommandPayloadSerializer serializer)
    {
        _bus = bus;
        _serializer = serializer;
    }

    public async Task EnqueueAsync<TCommand>(
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

        ct.ThrowIfCancellationRequested();
        await _bus.SendAsync(message);
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

        ct.ThrowIfCancellationRequested();
        await _bus.ScheduleAsync(
            new QueuedCommandMessage(
                envelope,
                ResolveCommandType<TCommand>(),
                _serializer.Serialize(command)),
            delay,
            new DeliveryOptions());
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
