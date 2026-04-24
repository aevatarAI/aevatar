using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

internal sealed class ChannelLlmReplyInboxRuntime :
    IHostedService,
    IAsyncDisposable,
    IChannelLlmReplyInbox
{
    internal const string InboxStreamId = "channel-runtime:llm-reply:inbox";

    private readonly IStreamProvider _streamProvider;
    private readonly IActorRuntime _actorRuntime;
    private readonly IConversationReplyGenerator _replyGenerator;
    private readonly IInteractiveReplyCollector? _interactiveReplyCollector;
    private readonly NyxIdRelayOptions? _relayOptions;
    private readonly ILogger<ChannelLlmReplyInboxRuntime> _logger;
    private IAsyncDisposable? _subscription;

    public ChannelLlmReplyInboxRuntime(
        IStreamProvider streamProvider,
        IActorRuntime actorRuntime,
        IConversationReplyGenerator replyGenerator,
        IInteractiveReplyCollector? interactiveReplyCollector,
        NyxIdRelayOptions? relayOptions,
        ILogger<ChannelLlmReplyInboxRuntime> logger)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _replyGenerator = replyGenerator ?? throw new ArgumentNullException(nameof(replyGenerator));
        _interactiveReplyCollector = interactiveReplyCollector;
        _relayOptions = relayOptions;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_subscription is not null)
            return;

        _subscription = await _streamProvider
            .GetStream(InboxStreamId)
            .SubscribeAsync<NeedsLlmReplyEvent>(ProcessAsync, ct);

        _logger.LogInformation("Started channel LLM reply inbox on {StreamId}", InboxStreamId);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_subscription is null)
            return;

        await _subscription.DisposeAsync();
        _subscription = null;
        _logger.LogInformation("Stopped channel LLM reply inbox on {StreamId}", InboxStreamId);
    }

    public Task EnqueueAsync(NeedsLlmReplyEvent request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _streamProvider.GetStream(InboxStreamId).ProduceAsync(request, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }

    internal const long MaxInboxRequestAgeMs = 5 * 60 * 1000;

    internal async Task ProcessAsync(NeedsLlmReplyEvent request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogInformation(
            "Processing LLM reply request: correlation={CorrelationId} target={TargetActorId}",
            request.CorrelationId,
            request.TargetActorId);

        if (request.Activity is null || string.IsNullOrWhiteSpace(request.TargetActorId))
        {
            _logger.LogWarning(
                "Dropping malformed deferred LLM reply request: correlation={CorrelationId}, target={TargetActorId}",
                request.CorrelationId,
                request.TargetActorId);
            await NotifyActorOfDropAsync(request, "malformed_deferred_llm_reply_request");
            return;
        }

        // Stale gate: NyxID relay reply tokens have a ~30 min TTL and the user access
        // token used for the LLM call expires inside ~15 min. A request that has been
        // sitting in the stream for hours can't lead to a successful reply, so drop it
        // here instead of spending an LLM round just to fail at the outbound stage.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (request.RequestedAtUnixMs > 0 && nowMs - request.RequestedAtUnixMs > MaxInboxRequestAgeMs)
        {
            _logger.LogInformation(
                "Dropping stale LLM reply request: correlation={CorrelationId} ageMs={AgeMs}",
                request.CorrelationId,
                nowMs - request.RequestedAtUnixMs);
            await NotifyActorOfDropAsync(request, "stale_inbox_request_dropped");
            return;
        }

        // Relay credential gate: relay turns require a fresh reply_token to send the
        // outbound. A relay request with no inbox-carried token (e.g., rehydrated from
        // persisted state after a pod restart that lost the original capture) cannot
        // be delivered, so skip the LLM call entirely.
        if (IsRelayRequest(request) && string.IsNullOrWhiteSpace(request.ReplyToken))
        {
            _logger.LogWarning(
                "Dropping relay LLM reply request without inbox-carried reply_token: correlation={CorrelationId}",
                request.CorrelationId);
            await NotifyActorOfDropAsync(request, "missing_relay_reply_token");
            return;
        }

        string replyText;
        MessageContent? outboundIntent = null;
        var terminalState = LlmReplyTerminalState.Completed;
        var errorCode = string.Empty;
        var errorSummary = string.Empty;

        try
        {
            var effectiveMetadata = BuildEffectiveMetadata(request);
            IDisposable? interactiveReplyScope = null;
            try
            {
                if (ShouldCaptureInteractiveReply(request.Activity))
                    interactiveReplyScope = _interactiveReplyCollector?.BeginScope();

                replyText = await _replyGenerator.GenerateReplyAsync(
                    request.Activity,
                    effectiveMetadata,
                    CancellationToken.None) ?? string.Empty;
                outboundIntent = _interactiveReplyCollector?.TryTake();
            }
            finally
            {
                interactiveReplyScope?.Dispose();
            }

            if (outboundIntent is null && string.IsNullOrWhiteSpace(replyText))
            {
                terminalState = LlmReplyTerminalState.Failed;
                errorCode = "empty_reply";
                errorSummary = "Reply generator returned an empty response.";
                replyText = "Sorry, I wasn't able to generate a response. Please try again.";
            }
        }
        catch (Exception ex)
        {
            terminalState = LlmReplyTerminalState.Failed;
            errorCode = "llm_reply_failed";
            errorSummary = ex.Message;
            replyText = NyxIdRelayErrorClassifier.Classify(ex.Message);
            _logger.LogWarning(
                ex,
                "Deferred LLM reply generation failed: correlation={CorrelationId}",
                request.CorrelationId);
        }

        var actor = await _actorRuntime.GetAsync(request.TargetActorId)
                    ?? await _actorRuntime.CreateAsync<ConversationGAgent>(request.TargetActorId, CancellationToken.None);
        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = request.CorrelationId,
            RegistrationId = request.RegistrationId,
            SourceActorId = InboxStreamId,
            Activity = request.Activity.Clone(),
            Outbound = outboundIntent?.Clone() ?? new MessageContent { Text = replyText },
            TerminalState = terminalState,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            ReadyAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            // Echo the inbox-only relay credential straight back so ConversationGAgent's
            // outbound reply does not depend on its in-memory token dict still having the
            // entry. The actor consumes these fields and never persists them.
            ReplyToken = request.ReplyToken ?? string.Empty,
            ReplyTokenExpiresAtUnixMs = request.ReplyTokenExpiresAtUnixMs,
        };
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(ready),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };

        await actor.HandleEventAsync(envelope, CancellationToken.None);
    }

    private IReadOnlyDictionary<string, string> BuildEffectiveMetadata(NeedsLlmReplyEvent request)
    {
        var metadata = new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal);
        var userAccessToken = request.Activity?.TransportExtras?.NyxUserAccessToken?.Trim();
        if (string.IsNullOrWhiteSpace(userAccessToken))
            return metadata;

        metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = userAccessToken;
        metadata[LLMRequestMetadataKeys.NyxIdOrgToken] = userAccessToken;
        return metadata;
    }

    private static bool IsRelayRequest(NeedsLlmReplyEvent request) =>
        request.Activity?.OutboundDelivery is
        {
            ReplyMessageId.Length: > 0,
            CorrelationId.Length: > 0,
        };

    private async Task NotifyActorOfDropAsync(NeedsLlmReplyEvent request, string reason)
    {
        if (string.IsNullOrWhiteSpace(request.TargetActorId) ||
            string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            return;
        }

        IActor? actor;
        try
        {
            actor = await _actorRuntime.GetAsync(request.TargetActorId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve actor for inbox drop notification: correlation={CorrelationId} target={TargetActorId}",
                request.CorrelationId,
                request.TargetActorId);
            return;
        }

        if (actor is null)
        {
            // No active actor means there is nothing pending to clean up; the request
            // either was never persisted or the actor's state was already retired.
            return;
        }

        var dropped = new DeferredLlmReplyDroppedEvent
        {
            CorrelationId = request.CorrelationId,
            Reason = reason,
            DroppedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(dropped),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };

        try
        {
            await actor.HandleEventAsync(envelope, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to deliver inbox drop notification: correlation={CorrelationId} reason={Reason}",
                request.CorrelationId,
                reason);
        }
    }

    private bool ShouldCaptureInteractiveReply(ChatActivity? activity)
    {
        if (_interactiveReplyCollector is null)
            return false;

        if (_relayOptions is { InteractiveRepliesEnabled: false })
            return false;

        return activity?.OutboundDelivery is
        {
            ReplyMessageId.Length: > 0,
            CorrelationId.Length: > 0,
        };
    }
}

internal sealed class ChannelLlmReplyInboxHostedService : IHostedService
{
    private readonly ChannelLlmReplyInboxRuntime _runtime;

    public ChannelLlmReplyInboxHostedService(ChannelLlmReplyInboxRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public Task StartAsync(CancellationToken ct) => _runtime.StartAsync(ct);

    public Task StopAsync(CancellationToken ct) => _runtime.StopAsync(ct);
}
