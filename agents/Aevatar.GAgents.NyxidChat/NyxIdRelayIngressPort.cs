using System.Security.Cryptography;
using System.Text;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace Aevatar.GAgents.NyxidChat;

internal sealed record NyxIdRelayIngressRequest(
    string ScopeId,
    ChatActivity Activity,
    string? ReplyToken,
    long ReplyTokenExpiresAtUnixMs,
    string? RelayApiKeyId,
    string? CallbackJti,
    long CallbackObservedAtUnixMs,
    long CallbackReplayExpiresAtUnixMs);

internal sealed record NyxIdRelayIngressAccepted(
    string MessageId,
    string ActorId);

internal interface INyxIdRelayIngressPort
{
    Task<NyxIdRelayIngressAccepted> AcceptAsync(
        NyxIdRelayIngressRequest request,
        CancellationToken ct = default);
}

// Refactor (iter56/cluster-868-endpoint-runtime-lifecycle): old=endpoint direct IActorRuntime, new=IGAgentDraftRunInteractionPort + CQRS Core
// The relay HTTP adapter now hands normalized NyxID callback data to a typed ingress port.
// Actor creation and envelope dispatch live behind this boundary, not in endpoint code.
// No NyxID external schema or CQRS Core cleanup semantics are changed.
internal sealed class NyxIdRelayIngressPort : INyxIdRelayIngressPort
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly ILogger<NyxIdRelayIngressPort> _logger;

    public NyxIdRelayIngressPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        ILogger<NyxIdRelayIngressPort> logger)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NyxIdRelayIngressAccepted> AcceptAsync(
        NyxIdRelayIngressRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var activity = request.Activity ?? throw new ArgumentNullException(nameof(request.Activity));
        if (string.IsNullOrWhiteSpace(activity.Conversation?.CanonicalKey))
        {
            throw new InvalidOperationException("Relay payload did not resolve to a canonical conversation key.");
        }

        var actorId = BuildScopedRelayConversationActorId(request.ScopeId, activity.Conversation.CanonicalKey);
        var actor = await _actorRuntime.CreateAsync<ConversationGAgent>(actorId, ct);
        var relayInbound = new NyxRelayInboundActivity
        {
            Activity = activity,
            ReplyToken = request.ReplyToken?.Trim() ?? string.Empty,
            ReplyTokenExpiresAtUnixMs = request.ReplyTokenExpiresAtUnixMs,
            CorrelationId = activity.OutboundDelivery?.CorrelationId ?? string.Empty,
            RelayApiKeyId = request.RelayApiKeyId ?? string.Empty,
            CallbackJti = request.CallbackJti ?? string.Empty,
            CallbackObservedAtUnixMs = request.CallbackObservedAtUnixMs,
            CallbackReplayExpiresAtUnixMs = request.CallbackReplayExpiresAtUnixMs,
        };
        var command = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(relayInbound),
            Route = EnvelopeRouteSemantics.CreateDirect("nyxid-chat.relay", actorId),
        };

        await _actorDispatchPort.DispatchAsync(actor.Id, command, ct);

        _logger.LogInformation(
            "Accepted relay callback into channel conversation backbone: message={MessageId}, actor={ActorId}, platform={Platform}, activity={ActivityType}",
            activity.Id,
            actorId,
            activity.ChannelId?.Value,
            activity.Type);

        return new NyxIdRelayIngressAccepted(activity.Id, actorId);
    }

    private static string BuildScopedRelayConversationActorId(string? scopeId, string canonicalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalKey);

        var scopeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopeId.Trim())))
            .ToLowerInvariant();
        return $"{ConversationGAgent.BuildActorId(canonicalKey)}:scope:{scopeHash}";
    }
}
