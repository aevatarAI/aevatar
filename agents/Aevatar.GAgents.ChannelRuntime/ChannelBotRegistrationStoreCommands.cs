using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChannelRuntime;

internal static class ChannelBotRegistrationStoreCommands
{
    public static Task DispatchRebuildProjectionAsync(
        IActorRuntime actorRuntime,
        string reason,
        CancellationToken ct = default) =>
        DispatchAsync(
            actorRuntime,
            new ChannelBotRebuildProjectionCommand
            {
                Reason = reason ?? string.Empty,
            },
            ct);

    public static Task DispatchUnregisterAsync(
        IActorRuntime actorRuntime,
        string registrationId,
        CancellationToken ct = default) =>
        DispatchAsync(
            actorRuntime,
            new ChannelBotUnregisterCommand
            {
                RegistrationId = registrationId ?? string.Empty,
            },
            ct);

    private static async Task DispatchAsync<TCommand>(
        IActorRuntime actorRuntime,
        TCommand command,
        CancellationToken ct)
        where TCommand : class, IMessage
    {
        ArgumentNullException.ThrowIfNull(actorRuntime);
        ArgumentNullException.ThrowIfNull(command);

        var actor = await GetOrCreateAsync(actorRuntime, ct);
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute
                {
                    TargetActorId = actor.Id,
                },
            },
        };

        await actor.HandleEventAsync(envelope, ct);
    }

    private static async Task<IActor> GetOrCreateAsync(IActorRuntime actorRuntime, CancellationToken ct)
    {
        return await actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
               ?? await actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                   ChannelBotRegistrationGAgent.WellKnownId,
                   ct);
    }
}
