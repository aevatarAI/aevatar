using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Runtime;

public static class ChannelBotRegistrationStoreCommands
{
    private const string PublisherActorId = "channel-runtime.registration-store";

    public static Task DispatchRegisterAsync(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        ChannelBotRegisterCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(
            actorRuntime,
            dispatchPort,
            command,
            ct);

    public static Task DispatchRebuildProjectionAsync(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        string reason,
        CancellationToken ct = default) =>
        DispatchAsync(
            actorRuntime,
            dispatchPort,
            new ChannelBotRebuildProjectionCommand
            {
                Reason = reason ?? string.Empty,
            },
            ct);

    public static Task DispatchUnregisterAsync(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        string registrationId,
        CancellationToken ct = default) =>
        DispatchAsync(
            actorRuntime,
            dispatchPort,
            new ChannelBotUnregisterCommand
            {
                RegistrationId = registrationId ?? string.Empty,
            },
            ct);

    private static async Task DispatchAsync<TCommand>(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        TCommand command,
        CancellationToken ct)
        where TCommand : class, IMessage
    {
        ArgumentNullException.ThrowIfNull(actorRuntime);
        ArgumentNullException.ThrowIfNull(dispatchPort);
        ArgumentNullException.ThrowIfNull(command);

        await EnsureStoreActorAsync(actorRuntime, ct);
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, ChannelBotRegistrationGAgent.WellKnownId),
        };

        await dispatchPort.DispatchAsync(ChannelBotRegistrationGAgent.WellKnownId, envelope, ct);
    }

    private static async Task EnsureStoreActorAsync(IActorRuntime actorRuntime, CancellationToken ct)
    {
        _ = await actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            ?? await actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                ChannelBotRegistrationGAgent.WellKnownId,
                ct);
    }
}
