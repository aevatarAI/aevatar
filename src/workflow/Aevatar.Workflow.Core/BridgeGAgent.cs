using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core;

public class BridgeGAgent : GAgentBase<BridgeState>
{
    private const string BridgeCallbackPublisherId = "bridge.callback";
    private readonly IActorRuntime _runtime;
    private readonly IBridgeCallbackTokenService _tokenService;

    public BridgeGAgent(
        IActorRuntime runtime,
        IBridgeCallbackTokenService tokenService)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleBridgeInboundCallbackReceived(BridgeInboundCallbackReceivedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        var hasValidToken = _tokenService.TryValidate(
            evt.CallbackToken ?? string.Empty,
            now,
            out var claims,
            out var validationError);
        var isExpiredToken = !hasValidToken &&
                             string.Equals(validationError, "token expired", StringComparison.OrdinalIgnoreCase) &&
                             !string.IsNullOrWhiteSpace(claims.TokenId);
        if (!hasValidToken && !isExpiredToken)
        {
            await PersistRejectedAsync(
                reason: string.IsNullOrWhiteSpace(validationError) ? "token validation failed" : validationError,
                callbackToken: evt.CallbackToken ?? string.Empty,
                source: evt.Source ?? string.Empty,
                sourceMessageId: evt.SourceMessageId ?? string.Empty,
                receivedAtUnixTimeMs: evt.ReceivedAtUnixTimeMs,
                tokenId: claims?.TokenId ?? string.Empty,
                CancellationToken.None);
            return;
        }

        var late = nowMs > claims.ExpiresAtUnixTimeMs;
        var actor = await _runtime.GetAsync(claims.ActorId);
        if (actor == null)
        {
            await PersistRejectedAsync(
                reason: $"workflow actor '{claims.ActorId}' not found",
                callbackToken: evt.CallbackToken ?? string.Empty,
                source: evt.Source ?? string.Empty,
                sourceMessageId: evt.SourceMessageId ?? string.Empty,
                receivedAtUnixTimeMs: evt.ReceivedAtUnixTimeMs,
                tokenId: claims.TokenId,
                CancellationToken.None);
            return;
        }

        try
        {
            await actor.HandleEventAsync(
                new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Payload = Any.Pack(new SignalReceivedEvent
                    {
                        RunId = claims.RunId,
                        StepId = claims.StepId,
                        SignalName = claims.SignalName,
                        Payload = evt.Payload ?? string.Empty,
                    }),
                    PublisherId = BridgeCallbackPublisherId,
                    Direction = EventDirection.Self,
                    CorrelationId = claims.TokenId,
                    TargetActorId = actor.Id,
                },
                CancellationToken.None);
            await PersistDomainEventAsync(
                new BridgeCallbackForwardedEvent
                {
                    TokenId = claims.TokenId,
                    ActorId = claims.ActorId,
                    RunId = claims.RunId,
                    StepId = claims.StepId,
                    SignalName = claims.SignalName,
                    Late = late,
                    Payload = evt.Payload ?? string.Empty,
                    Source = evt.Source ?? string.Empty,
                    SourceMessageId = evt.SourceMessageId ?? string.Empty,
                    SourceChatId = evt.SourceChatId ?? string.Empty,
                    SourceUserId = evt.SourceUserId ?? string.Empty,
                    ReceivedAtUnixTimeMs = evt.ReceivedAtUnixTimeMs,
                    ForwardedAtUnixTimeMs = nowMs,
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            await PersistRejectedAsync(
                reason: $"bridge forwarding failed: {ex.Message}",
                callbackToken: evt.CallbackToken ?? string.Empty,
                source: evt.Source ?? string.Empty,
                sourceMessageId: evt.SourceMessageId ?? string.Empty,
                receivedAtUnixTimeMs: evt.ReceivedAtUnixTimeMs,
                tokenId: claims.TokenId,
                CancellationToken.None);
        }
    }

    protected override BridgeState TransitionState(BridgeState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<BridgeCallbackForwardedEvent>(ApplyForwarded)
            .On<BridgeCallbackRejectedEvent>(ApplyRejected)
            .OrCurrent();

    private static BridgeState ApplyForwarded(BridgeState current, BridgeCallbackForwardedEvent evt)
    {
        var next = current.Clone();
        next.TotalReceived = current.TotalReceived + 1;
        next.TotalForwarded = current.TotalForwarded + 1;
        next.TotalLate = current.TotalLate + (evt.Late ? 1 : 0);
        next.LastAppliedEventVersion = current.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat("forwarded:", evt.TokenId ?? string.Empty);
        return next;
    }

    private static BridgeState ApplyRejected(BridgeState current, BridgeCallbackRejectedEvent evt)
    {
        var next = current.Clone();
        next.TotalReceived = current.TotalReceived + 1;
        next.TotalRejected = current.TotalRejected + 1;
        next.LastAppliedEventVersion = current.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat("rejected:", evt.TokenId ?? string.Empty);
        return next;
    }

    private Task PersistRejectedAsync(
        string reason,
        string callbackToken,
        string source,
        string sourceMessageId,
        long receivedAtUnixTimeMs,
        string tokenId,
        CancellationToken ct)
    {
        return PersistDomainEventAsync(
            new BridgeCallbackRejectedEvent
            {
                Reason = reason ?? string.Empty,
                CallbackToken = callbackToken ?? string.Empty,
                Source = source ?? string.Empty,
                SourceMessageId = sourceMessageId ?? string.Empty,
                ReceivedAtUnixTimeMs = receivedAtUnixTimeMs,
                TokenId = tokenId ?? string.Empty,
            },
            ct);
    }
}
