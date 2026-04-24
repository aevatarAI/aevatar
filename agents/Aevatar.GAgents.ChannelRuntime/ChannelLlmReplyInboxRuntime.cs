using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Application.Studio.Abstractions;
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
    private readonly Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? _relayOptions;
    private readonly INyxIdRelayScopeResolver? _scopeResolver;
    private readonly IUserConfigQueryPort? _userConfigQueryPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChannelLlmReplyInboxRuntime> _logger;
    private IAsyncDisposable? _subscription;

    public ChannelLlmReplyInboxRuntime(
        IStreamProvider streamProvider,
        IActorRuntime actorRuntime,
        IConversationReplyGenerator replyGenerator,
        IInteractiveReplyCollector? interactiveReplyCollector,
        Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? relayOptions,
        ILogger<ChannelLlmReplyInboxRuntime> logger,
        INyxIdRelayScopeResolver? scopeResolver = null,
        IUserConfigQueryPort? userConfigQueryPort = null,
        TimeProvider? timeProvider = null)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _replyGenerator = replyGenerator ?? throw new ArgumentNullException(nameof(replyGenerator));
        _interactiveReplyCollector = interactiveReplyCollector;
        _relayOptions = relayOptions;
        _scopeResolver = scopeResolver;
        _userConfigQueryPort = userConfigQueryPort;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

        var actor = await _actorRuntime.GetAsync(request.TargetActorId)
                    ?? await _actorRuntime.CreateAsync<ConversationGAgent>(request.TargetActorId, CancellationToken.None);

        string replyText;
        MessageContent? outboundIntent = null;
        var terminalState = LlmReplyTerminalState.Completed;
        var errorCode = string.Empty;
        var errorSummary = string.Empty;
        TurnStreamingReplySink? streamingSink = TryBuildStreamingSink(request, actor);

        try
        {
            var effectiveMetadata = await BuildEffectiveMetadataAsync(request, CancellationToken.None);
            IDisposable? interactiveReplyScope = null;
            try
            {
                if (ShouldCaptureInteractiveReply(request.Activity))
                    interactiveReplyScope = _interactiveReplyCollector?.BeginScope();

                replyText = await _replyGenerator.GenerateReplyAsync(
                    request.Activity,
                    effectiveMetadata,
                    streamingSink,
                    CancellationToken.None) ?? string.Empty;
                outboundIntent = _interactiveReplyCollector?.TryTake();
            }
            finally
            {
                interactiveReplyScope?.Dispose();
            }

            if (streamingSink is not null &&
                outboundIntent is null &&
                !string.IsNullOrWhiteSpace(replyText))
            {
                await streamingSink.FinalizeAsync(replyText, CancellationToken.None);
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
            ReadyAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            // Echo the inbox-only relay credential straight back so ConversationGAgent's
            // outbound reply does not depend on its in-memory token dict still having the
            // entry. The actor consumes these fields and never persists them.
            ReplyToken = request.ReplyToken ?? string.Empty,
            ReplyTokenExpiresAtUnixMs = request.ReplyTokenExpiresAtUnixMs,
        };
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(ready),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };

        await actor.HandleEventAsync(envelope, CancellationToken.None);
    }

    private TurnStreamingReplySink? TryBuildStreamingSink(NeedsLlmReplyEvent request, IActor targetActor)
    {
        if (_relayOptions is not { StreamingRepliesEnabled: true })
            return null;
        if (request.Activity?.OutboundDelivery is not
            {
                ReplyMessageId.Length: > 0,
                CorrelationId.Length: > 0,
            })
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(request.CorrelationId))
            return null;

        var throttle = TimeSpan.FromMilliseconds(Math.Max(0, _relayOptions.StreamingFlushIntervalMs));
        return new TurnStreamingReplySink(
            targetActor,
            request.CorrelationId,
            request.RegistrationId,
            request.Activity.Clone(),
            throttle,
            _timeProvider,
            _logger);
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildEffectiveMetadataAsync(
        NeedsLlmReplyEvent request,
        CancellationToken ct)
    {
        var metadata = new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal);

        // Apply the bot owner's pre-configured LLM route + model. The relay callback
        // identifies the bot by api_key_id (in activity.Bot.Value); we resolve that to
        // the owner's Aevatar scope id and load the same UserConfig the owner uses
        // when chatting through nyxid-chat themselves, then pin ModelOverride /
        // NyxIdRoutePreference / MaxToolRoundsOverride from that configuration.
        await ApplyBotOwnerLlmConfigAsync(request, metadata, ct);

        // The inbound callback's X-NyxID-User-Token is the bot owner's NyxID session
        // JWT (freshly issued by NyxID for each callback). It is the bot owner's own
        // credential for LLM calls — the same thing that would authorize them in
        // nyxid-chat. The short TTL (~15 min) is mitigated by the direct-enqueue
        // dispatch (#380), the inbox-echoed token flow (#383), and the stale pending
        // request GC, so the token is still valid when the LLM call actually fires
        // for any non-stale request. If the downstream provider rejects it, the
        // classifier surfaces a real user-facing error via NyxIdRelayErrorClassifier.
        var userAccessToken = request.Activity?.TransportExtras?.NyxUserAccessToken?.Trim();
        if (!string.IsNullOrWhiteSpace(userAccessToken))
        {
            metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = userAccessToken;
            metadata[LLMRequestMetadataKeys.NyxIdOrgToken] = userAccessToken;
        }

        return metadata;
    }

    private async Task ApplyBotOwnerLlmConfigAsync(
        NeedsLlmReplyEvent request,
        IDictionary<string, string> metadata,
        CancellationToken ct)
    {
        if (_scopeResolver is null || _userConfigQueryPort is null)
            return;

        var apiKeyId = request.Activity?.Bot?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(apiKeyId))
            return;

        string? scopeId;
        try
        {
            scopeId = await _scopeResolver.ResolveScopeIdByApiKeyAsync(apiKeyId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve bot owner scope id for LLM config: correlation={CorrelationId} apiKeyId={ApiKeyId}",
                request.CorrelationId,
                apiKeyId);
            return;
        }

        if (string.IsNullOrWhiteSpace(scopeId))
        {
            _logger.LogDebug(
                "No bot owner scope id resolved for LLM config: correlation={CorrelationId} apiKeyId={ApiKeyId}",
                request.CorrelationId,
                apiKeyId);
            return;
        }

        try
        {
            var config = await _userConfigQueryPort.GetAsync(scopeId, ct);
            if (!string.IsNullOrWhiteSpace(config.DefaultModel))
                metadata[LLMRequestMetadataKeys.ModelOverride] = config.DefaultModel.Trim();
            if (!string.IsNullOrWhiteSpace(config.PreferredLlmRoute))
                metadata[LLMRequestMetadataKeys.NyxIdRoutePreference] = config.PreferredLlmRoute.Trim();
            if (config.MaxToolRounds > 0)
                metadata[LLMRequestMetadataKeys.MaxToolRoundsOverride] =
                    config.MaxToolRounds.ToString(System.Globalization.CultureInfo.InvariantCulture);

            _logger.LogInformation(
                "Applied bot owner LLM config: correlation={CorrelationId} scopeId={ScopeId} model={Model} route={Route}",
                request.CorrelationId,
                scopeId,
                string.IsNullOrWhiteSpace(config.DefaultModel) ? "<server-default>" : config.DefaultModel,
                string.IsNullOrWhiteSpace(config.PreferredLlmRoute) ? "<server-default>" : config.PreferredLlmRoute);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load bot owner LLM config: correlation={CorrelationId} scopeId={ScopeId}",
                request.CorrelationId,
                scopeId);
        }
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
