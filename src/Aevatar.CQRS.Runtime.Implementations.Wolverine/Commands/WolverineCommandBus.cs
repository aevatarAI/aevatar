using Aevatar.CQRS.Runtime.Abstractions.Commands;
using System.Text.Json;
using Wolverine;
using AevatarCommandBus = Aevatar.CQRS.Runtime.Abstractions.Commands.ICommandBus;

namespace Aevatar.CQRS.Runtime.Implementations.Wolverine.Commands;

internal sealed class WolverineCommandBus : AevatarCommandBus, ICommandScheduler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IMessageBus _bus;

    public WolverineCommandBus(IMessageBus bus)
    {
        _bus = bus;
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
            JsonSerializer.Serialize(command, JsonOptions));

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
                JsonSerializer.Serialize(command, JsonOptions)),
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
