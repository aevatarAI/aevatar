using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChatRouting;

/// <summary>
/// Runtime-backed command port for <see cref="ChatRoutePolicyGAgent"/>.
/// </summary>
// Refactor (iter34/cluster-005-mainnet-host-direct-actor-runtime):
//   Old pattern: Mainnet Host endpoints inject IActorRuntime/IActorDispatchPort and build EventEnvelope + dispatch directly in Host code.
//   New principle: Host calls Application command ports that normalize, resolve target, build envelope, dispatch, return honest accepted receipt.
//   Host endpoint stays minimal (auth + body parsing). NO direct dependency on IActorRuntime/IActorDispatchPort in Host.
internal sealed class ChatRoutePolicyCommandPort : IChatRoutePolicyCommandPort
{
    private const string ActorIdPrefix = "chat-route-policy:";
    private const string PublisherActorId = "chat-route-policy-admin";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;

    public ChatRoutePolicyCommandPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
    }

    public Task<ChatRoutePolicyCommandAcceptedReceipt> UpsertAsync(
        string scopeId,
        UpsertChatRoutePolicyRequested command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return DispatchAsync(scopeId, command, ct);
    }

    public Task<ChatRoutePolicyCommandAcceptedReceipt> UpsertRuleAsync(
        string scopeId,
        UpsertChatRouteRuleRequested command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return DispatchAsync(scopeId, command, ct);
    }

    public Task<ChatRoutePolicyCommandAcceptedReceipt> RemoveRuleAsync(
        string scopeId,
        RemoveChatRouteRuleRequested command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return DispatchAsync(scopeId, command, ct);
    }

    private async Task<ChatRoutePolicyCommandAcceptedReceipt> DispatchAsync(
        string scopeId,
        IMessage command,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("scopeId is required.", nameof(scopeId));

        var actorId = $"{ActorIdPrefix}{scopeId.Trim()}";
        var actor = await _actorRuntime.CreateAsync<ChatRoutePolicyGAgent>(actorId, ct);
        var commandId = Guid.NewGuid().ToString("N");
        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, actor.Id),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = commandId,
            },
            Runtime = new EnvelopeRuntime
            {
                DeliveryIdentity = new DeliveryIdentity
                {
                    OperationId = commandId,
                },
            },
        };

        await _actorDispatchPort.DispatchAsync(actor.Id, envelope, ct);
        return new ChatRoutePolicyCommandAcceptedReceipt(actor.Id, commandId, commandId);
    }
}
