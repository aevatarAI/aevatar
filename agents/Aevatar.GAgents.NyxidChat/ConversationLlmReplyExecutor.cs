using Aevatar.Foundation.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Drives one LLM reply turn end-to-end on behalf of <see cref="ConversationGAgent"/>:
/// pre-LLM gates (stale age, missing relay token, malformed payload), bot-owner config
/// enrichment, the LLM call itself, streaming-sink wiring, and dispatch of the terminal
/// signal back to the originating actor.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the silo-wide <c>ChannelLlmReplyInboxRuntime</c> stream subscriber. The actor
/// invokes <see cref="StartAsync"/> from inside its turn; the executor schedules the LLM
/// work on a background task so the 60-300s call cannot pin the actor turn, then
/// dispatches an <c>LlmReplyReadyEvent</c> (or <c>DeferredLlmReplyDroppedEvent</c>) back
/// to <c>request.TargetActorId</c> via <see cref="IActorDispatchPort"/>.
/// </para>
/// <para>
/// The background task only does external I/O and finishes by signalling the actor; it
/// never reads or writes actor state directly. All actor-state mutations happen inside
/// the actor's handler when the dispatched event arrives, preserving the actor's
/// single-threaded execution invariant.
/// </para>
/// </remarks>
public sealed class ConversationLlmReplyExecutor : IConversationLlmReplyExecutor
{
    internal const string PublisherActorId = "channel-runtime.llm-reply-executor";

    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly IConversationReplyGenerator _replyGenerator;
    private readonly IInteractiveReplyCollector? _interactiveReplyCollector;
    private readonly Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? _relayOptions;
    private readonly INyxIdRelayScopeResolver? _scopeResolver;
    private readonly IUserConfigQueryPort? _userConfigQueryPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConversationLlmReplyExecutor> _logger;

    public ConversationLlmReplyExecutor(
        IActorDispatchPort actorDispatchPort,
        IConversationReplyGenerator replyGenerator,
        IInteractiveReplyCollector? interactiveReplyCollector,
        Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? relayOptions,
        ILogger<ConversationLlmReplyExecutor> logger,
        INyxIdRelayScopeResolver? scopeResolver = null,
        IUserConfigQueryPort? userConfigQueryPort = null,
        TimeProvider? timeProvider = null)
    {
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _replyGenerator = replyGenerator ?? throw new ArgumentNullException(nameof(replyGenerator));
        _interactiveReplyCollector = interactiveReplyCollector;
        _relayOptions = relayOptions;
        _scopeResolver = scopeResolver;
        _userConfigQueryPort = userConfigQueryPort;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Pending LLM reply requests older than this are considered stale and dropped before
    /// the LLM call: NyxID relay reply tokens have a ~30 min TTL and the user access token
    /// used for the LLM call expires inside ~15 min, so a request that has been waiting for
    /// hours cannot lead to a successful reply.
    /// </summary>
    internal const long MaxRequestAgeMs = 5 * 60 * 1000;

    /// <summary>
    /// Hard upper bound on a single LLM reply turn. Mirrors
    /// <see cref="Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions.ResponseTimeoutSeconds"/> (default 300s) — long enough for the
    /// aevatar Lark bot's multi-step flows (skill search + remote tool + summarize) to land
    /// without truncation, short enough that a true hang does not pin the turn forever.
    /// A configured value of <c>0</c> or negative is treated as "disable the cap" — pass
    /// through with no timeout, mirroring HttpClient/Polly conventions where 0 means
    /// "no limit". The default of 300s applies when the option is unset.
    /// </summary>
    internal const int FallbackTimeoutSecondsDefault = 300;

    /// <summary>
    /// Standalone budget for metadata enrichment (scope resolve + UserConfig lookup).
    /// We split this out from the LLM run budget so that slow infra around metadata
    /// can't silently steal the LLM's response window — and so a metadata timeout
    /// surfaces as a distinct error code rather than a misleading "llm_reply_timeout".
    /// </summary>
    internal static readonly TimeSpan MetadataBuildBudget = TimeSpan.FromSeconds(15);

    public Task StartAsync(NeedsLlmReplyEvent request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Honor the caller's token: a turn cancelled before scheduling shouldn't burn an
        // LLM round. If the token is already signaled, throw before cloning so the actor
        // sees the cancellation directly instead of via a swallowed background error.
        ct.ThrowIfCancellationRequested();

        // Snapshot the request: the caller may mutate the original (e.g. the actor
        // re-enriches with a fresh reply token on retry) and we are about to hand off
        // ownership to a background task.
        var snapshot = request.Clone();

        // Use Task.Run so the LLM work runs on the thread pool, not on the caller's
        // synchronization context (which, for an actor turn, must not be blocked or the
        // very bottleneck this seam removes is reintroduced inside one actor). The
        // background task only does external I/O and finishes by dispatching back to
        // the actor — it never reads or writes actor state. Passing `ct` means a
        // cancellation between StartAsync and the scheduler picking up the work skips
        // the LLM round entirely; once the task is already running, internal cancellation
        // tokens (metadata budget, fallback timeout) take over.
        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessAsync(snapshot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // ProcessAsync handles its own errors and dispatches a terminal signal;
                // an exception bubbling out here means dispatch itself or some outer
                // step failed unexpectedly. The IConversationLlmReplyExecutor contract
                // requires a terminal signal so the actor can retire its pending entry —
                // without it, State.PendingLlmReplyRequests leaks until the 5-minute
                // stale-age gate kicks in on the next activation. Send the drop notice
                // directly; the stale gate becomes a fallback if even the drop dispatch
                // fails.
                _logger.LogError(
                    ex,
                    "Conversation LLM reply executor crashed before dispatching terminal signal: correlation={CorrelationId} target={TargetActorId}",
                    snapshot.CorrelationId,
                    snapshot.TargetActorId);
                try
                {
                    await NotifyActorOfDropAsync(snapshot, "executor_crash").ConfigureAwait(false);
                }
                catch (Exception notifyEx)
                {
                    _logger.LogError(
                        notifyEx,
                        "Failed to notify actor of executor crash; pending entry will retire via 5-min stale-age gate: correlation={CorrelationId}",
                        snapshot.CorrelationId);
                }
            }
        }, ct);

        return Task.CompletedTask;
    }

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

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (request.RequestedAtUnixMs > 0 && nowMs - request.RequestedAtUnixMs > MaxRequestAgeMs)
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
                "Dropping relay LLM reply request without reply_token: correlation={CorrelationId}",
                request.CorrelationId);
            await NotifyActorOfDropAsync(request, "missing_relay_reply_token");
            return;
        }

        string replyText;
        MessageContent? outboundIntent = null;
        var terminalState = LlmReplyTerminalState.Completed;
        var errorCode = string.Empty;
        var errorSummary = string.Empty;
        using TurnStreamingReplySink? streamingSink = TryBuildStreamingSink(request, request.TargetActorId);

        // Metadata enrichment runs on its own short budget so a slow scope/UserConfig lookup
        // can't silently shrink the LLM run's window. The LLM CTS only starts ticking after
        // metadata is in hand, and a metadata timeout surfaces as a distinct error code.
        IReadOnlyDictionary<string, string> effectiveMetadata;
        using (var metadataCts = new CancellationTokenSource(MetadataBuildBudget))
        {
            try
            {
                effectiveMetadata = await BuildEffectiveMetadataAsync(request, metadataCts.Token);
            }
            catch (OperationCanceledException ex) when (metadataCts.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "Deferred LLM reply metadata build timed out after {TimeoutSeconds}s: correlation={CorrelationId}",
                    (int)MetadataBuildBudget.TotalSeconds,
                    request.CorrelationId);
                replyText = "Sorry, I couldn't load your model preferences in time. Please try again.";
                terminalState = LlmReplyTerminalState.Failed;
                errorCode = "llm_reply_metadata_timeout";
                errorSummary = $"Metadata enrichment exceeded {(int)MetadataBuildBudget.TotalSeconds}s budget.";
                await DispatchReadyEventAsync(request, replyText, outboundIntent, terminalState, errorCode, errorSummary);
                return;
            }
        }

        var fallbackTimeout = ResolveFallbackTimeout();
        using var timeoutCts = fallbackTimeout > TimeSpan.Zero
            ? new CancellationTokenSource(fallbackTimeout)
            : new CancellationTokenSource();

        try
        {
            IDisposable? interactiveReplyScope = null;
            try
            {
                if (ShouldCaptureInteractiveReply(request.Activity))
                    interactiveReplyScope = _interactiveReplyCollector?.BeginScope();

                replyText = await _replyGenerator.GenerateReplyAsync(
                    request.Activity,
                    effectiveMetadata,
                    streamingSink,
                    timeoutCts.Token) ?? string.Empty;
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
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            terminalState = LlmReplyTerminalState.Failed;
            errorCode = "llm_reply_timeout";
            errorSummary = $"LLM reply generation exceeded {(int)fallbackTimeout.TotalSeconds}s budget.";
            replyText = "Sorry, this took too long to process — the model or one of its tools didn't " +
                        "respond in time. Please try again, or rephrase the request.";
            _logger.LogWarning(
                ex,
                "Deferred LLM reply timed out after {TimeoutSeconds}s: correlation={CorrelationId}",
                (int)fallbackTimeout.TotalSeconds,
                request.CorrelationId);
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

        await DispatchReadyEventAsync(request, replyText, outboundIntent, terminalState, errorCode, errorSummary);
    }

    private async Task DispatchReadyEventAsync(
        NeedsLlmReplyEvent request,
        string replyText,
        MessageContent? outboundIntent,
        LlmReplyTerminalState terminalState,
        string errorCode,
        string errorSummary)
    {
        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = request.CorrelationId,
            RegistrationId = request.RegistrationId,
            SourceActorId = PublisherActorId,
            Activity = request.Activity!.Clone(),
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
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, request.TargetActorId),
        };

        await _actorDispatchPort.DispatchAsync(request.TargetActorId, envelope, CancellationToken.None);
    }

    private TurnStreamingReplySink? TryBuildStreamingSink(NeedsLlmReplyEvent request, string targetActorId)
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

        var cardMode = _relayOptions.StreamingCardKitEnabled;
        var throttle = TimeSpan.FromMilliseconds(Math.Max(0, cardMode
            ? _relayOptions.StreamingCardKitFlushIntervalMs
            : _relayOptions.StreamingFlushIntervalMs));
        // CardKit element-content updates have no per-card edit cap, so the interim cap that
        // protects the legacy edit-message path is irrelevant. Pass int.MaxValue so the sink's
        // throttle is the only frame-rate gate.
        var maxInterimChunks = cardMode
            ? int.MaxValue
            : Math.Max(0, _relayOptions.StreamingMaxInterimChunks);
        return new TurnStreamingReplySink(
            _actorDispatchPort,
            targetActorId,
            request.CorrelationId,
            request.RegistrationId,
            request.Activity.Clone(),
            throttle,
            _timeProvider,
            _logger,
            maxInterimChunks,
            cardMode);
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

    /// <summary>
    /// Resolve the LLM-run cap from <see cref="Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions.ResponseTimeoutSeconds"/>.
    /// Conventions:
    ///   * unset / null  → <see cref="FallbackTimeoutSecondsDefault"/> (300s)
    ///   * &gt; 0        → use that exact value
    ///   * 0 or negative → <see cref="TimeSpan.Zero"/> meaning "no timeout"; the caller
    ///     constructs an unbounded <see cref="CancellationTokenSource"/>. Use this only
    ///     in environments that have an external watchdog — without it, a hung tool
    ///     keeps the executor task alive indefinitely.
    /// </summary>
    private TimeSpan ResolveFallbackTimeout()
    {
        if (_relayOptions is null)
            return TimeSpan.FromSeconds(FallbackTimeoutSecondsDefault);
        var configured = _relayOptions.ResponseTimeoutSeconds;
        if (configured <= 0)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds(configured);
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
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, request.TargetActorId),
        };

        try
        {
            await _actorDispatchPort.DispatchAsync(request.TargetActorId, envelope, CancellationToken.None);
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
