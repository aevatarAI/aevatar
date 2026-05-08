using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Run-scoped continuation owner for one deferred channel LLM reply.
/// </summary>
public sealed class AgentRunGAgent : GAgentBase<AgentRunGAgentState>
{
    public const string ActorIdPrefix = "channel-agent-run:";

    internal const long MaxRunRequestAgeMs = 5 * 60 * 1000;

    /// <summary>
    /// Hard upper bound on a single LLM reply turn. Mirrors
    /// <c>NyxIdRelayOptions.ResponseTimeoutSeconds</c> (default 300s).
    /// A configured value of <c>0</c> or negative is treated as "disable the cap".
    /// </summary>
    internal const int FallbackTimeoutSecondsDefault = 300;

    /// <summary>
    /// Standalone budget for metadata enrichment (scope resolve + UserConfig lookup).
    /// </summary>
    internal static readonly TimeSpan MetadataBuildBudget = TimeSpan.FromSeconds(15);

    internal static readonly TimeSpan TerminalCleanupDelay = TimeSpan.FromMinutes(5);
    private const string TerminalCleanupCallbackPrefix = "agent-run-terminal-cleanup";
    internal static readonly TimeSpan OutputDispatchRetryDelay = TimeSpan.FromSeconds(5);
    private const string OutputDispatchRetryCallbackPrefix = "agent-run-output-dispatch-retry";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly IConversationReplyGenerator _replyGenerator;
    private readonly IInteractiveReplyCollector? _interactiveReplyCollector;
    private readonly Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? _relayOptions;
    private readonly INyxIdRelayScopeResolver? _scopeResolver;
    private readonly IUserConfigQueryPort? _userConfigQueryPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentRunGAgent> _logger;

    public AgentRunGAgent(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        IConversationReplyGenerator replyGenerator,
        IInteractiveReplyCollector? interactiveReplyCollector,
        Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? relayOptions,
        ILogger<AgentRunGAgent> logger,
        INyxIdRelayScopeResolver? scopeResolver = null,
        IUserConfigQueryPort? userConfigQueryPort = null,
        TimeProvider? timeProvider = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _replyGenerator = replyGenerator ?? throw new ArgumentNullException(nameof(replyGenerator));
        _interactiveReplyCollector = interactiveReplyCollector;
        _relayOptions = relayOptions;
        _scopeResolver = scopeResolver;
        _userConfigQueryPort = userConfigQueryPort;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static string BuildActorId(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return ActorIdPrefix + correlationId.Trim();
    }

    protected override AgentRunGAgentState TransitionState(AgentRunGAgentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<AgentRunStartedEvent>(ApplyStarted)
            .On<AgentRunReplyProducedEvent>(ApplyReplyProduced)
            .On<AgentRunDroppedEvent>(ApplyDropped)
            .On<AgentRunFailedEvent>(ApplyFailed)
            .OrCurrent();

    [EventHandler]
    public async Task HandleStartAsync(AgentRunStartRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Request is null)
        {
            _logger.LogWarning("Dropping malformed agent run start command without request: runActor={RunActorId}", Id);
            return;
        }

        var request = command.Request.Clone();
        var runId = NormalizeOptional(request.CorrelationId) ?? Id;
        var startedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        if (State.Status is AgentRunStatus.ReplyProduced or AgentRunStatus.Dropped or AgentRunStatus.Failed)
        {
            _logger.LogInformation(
                "Ignoring duplicate terminal agent run start: runId={RunId} status={Status}",
                runId,
                State.Status);
            await ScheduleTerminalCleanupAsync(NormalizeOptional(State.RunId) ?? runId);
            return;
        }

        if (string.IsNullOrWhiteSpace(State.RunId))
        {
            await PersistDomainEventAsync(new AgentRunStartedEvent
            {
                RunId = runId,
                CorrelationId = request.CorrelationId,
                TargetActorId = request.TargetActorId,
                StartedAtUnixMs = startedAtUnixMs,
            });
        }

        try
        {
            await ProcessAsync(request, runId);
        }
        catch (AgentRunOutputDispatchException ex)
        {
            if (!await TryHandleOutputDispatchFailureAsync(request, runId, ex))
                throw;
        }
        catch (Exception ex)
        {
            await FailAfterUnexpectedExceptionAsync(request, runId, ex);
        }
    }

    [EventHandler]
    public async Task HandleCleanupAsync(AgentRunCleanupRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (State.Status is not (AgentRunStatus.ReplyProduced or AgentRunStatus.Dropped or AgentRunStatus.Failed))
            return;
        if (!string.IsNullOrWhiteSpace(command.RunId) &&
            !string.IsNullOrWhiteSpace(State.RunId) &&
            !string.Equals(command.RunId, State.RunId, StringComparison.Ordinal))
        {
            return;
        }

        await _actorRuntime.DestroyAsync(Id, CancellationToken.None);
    }

    private async Task ProcessAsync(NeedsLlmReplyEvent request, string runId)
    {
        _logger.LogInformation(
            "Processing agent run LLM reply request: runId={RunId} correlation={CorrelationId} target={TargetActorId}",
            runId,
            request.CorrelationId,
            request.TargetActorId);

        if (request.Activity is null || string.IsNullOrWhiteSpace(request.TargetActorId))
        {
            _logger.LogWarning(
                "Dropping malformed deferred LLM reply request: runId={RunId}, correlation={CorrelationId}, target={TargetActorId}",
                runId,
                request.CorrelationId,
                request.TargetActorId);
            await DropAsync(request, runId, "malformed_deferred_llm_reply_request");
            return;
        }

        // Stale gate: NyxID relay reply tokens have a ~30 min TTL and the user access
        // token used for the LLM call expires inside ~15 min. A request that has been
        // delayed past the run window cannot lead to a successful reply.
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (request.RequestedAtUnixMs > 0 && nowMs - request.RequestedAtUnixMs > MaxRunRequestAgeMs)
        {
            _logger.LogInformation(
                "Dropping stale LLM reply request: runId={RunId} correlation={CorrelationId} ageMs={AgeMs}",
                runId,
                request.CorrelationId,
                nowMs - request.RequestedAtUnixMs);
            await DropAsync(request, runId, "stale_agent_run_request_dropped");
            return;
        }

        // Relay credential gate: relay turns require a fresh reply_token to send the
        // outbound. A relay request with no command-carried token cannot be delivered,
        // so skip the LLM call entirely.
        if (IsRelayRequest(request) && string.IsNullOrWhiteSpace(request.ReplyToken))
        {
            _logger.LogWarning(
                "Dropping relay LLM reply request without command-carried reply_token: runId={RunId} correlation={CorrelationId}",
                runId,
                request.CorrelationId);
            await DropAsync(request, runId, "missing_relay_reply_token");
            return;
        }

        await EnsureTargetActorAsync(request.TargetActorId);

        string replyText;
        MessageContent? outboundIntent = null;
        var terminalState = LlmReplyTerminalState.Completed;
        var errorCode = string.Empty;
        var errorSummary = string.Empty;
        using TurnStreamingReplySink? streamingSink = TryBuildStreamingSink(request, request.TargetActorId);

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
                    "Deferred LLM reply metadata build timed out after {TimeoutSeconds}s: runId={RunId} correlation={CorrelationId}",
                    (int)MetadataBuildBudget.TotalSeconds,
                    runId,
                    request.CorrelationId);
                replyText = "Sorry, I couldn't load your model preferences in time. Please try again.";
                terminalState = LlmReplyTerminalState.Failed;
                errorCode = "llm_reply_metadata_timeout";
                errorSummary = $"Metadata enrichment exceeded {(int)MetadataBuildBudget.TotalSeconds}s budget.";
                await FailAndDispatchReadyAsync(request, runId, replyText, outboundIntent, terminalState, errorCode, errorSummary);
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
            replyText = "Sorry, this took too long to process - the model or one of its tools didn't " +
                        "respond in time. Please try again, or rephrase the request.";
            _logger.LogWarning(
                ex,
                "Deferred LLM reply timed out after {TimeoutSeconds}s: runId={RunId} correlation={CorrelationId}",
                (int)fallbackTimeout.TotalSeconds,
                runId,
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
                "Deferred LLM reply generation failed: runId={RunId} correlation={CorrelationId}",
                runId,
                request.CorrelationId);
        }

        if (terminalState == LlmReplyTerminalState.Failed)
        {
            await FailAndDispatchReadyAsync(
                request,
                runId,
                replyText,
                outboundIntent,
                terminalState,
                errorCode,
                errorSummary);
            return;
        }

        await DispatchReadyEventAsync(request, replyText, outboundIntent, terminalState, errorCode, errorSummary);
        await PersistReplyProducedAsync(request, runId, terminalState, errorCode, errorSummary);
    }

    private async Task FailAndDispatchReadyAsync(
        NeedsLlmReplyEvent request,
        string runId,
        string replyText,
        MessageContent? outboundIntent,
        LlmReplyTerminalState terminalState,
        string errorCode,
        string errorSummary)
    {
        await DispatchReadyEventAsync(request, replyText, outboundIntent, terminalState, errorCode, errorSummary);
        await PersistFailedAsync(request, runId, errorCode, errorSummary);
    }

    private async Task DropAsync(NeedsLlmReplyEvent request, string runId, string reason)
    {
        if (CanNotifyDrop(request))
            await DispatchDropNotificationAsync(request, reason);

        await PersistDomainEventAsync(new AgentRunDroppedEvent
        {
            RunId = runId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            Reason = reason,
            DroppedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });

        await ScheduleTerminalCleanupAsync(runId);
    }

    private async Task PersistReplyProducedAsync(
        NeedsLlmReplyEvent request,
        string runId,
        LlmReplyTerminalState terminalState,
        string errorCode,
        string errorSummary)
    {
        await PersistDomainEventAsync(new AgentRunReplyProducedEvent
        {
            RunId = runId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            TerminalState = terminalState,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            ProducedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });

        await ScheduleTerminalCleanupAsync(runId);
    }

    private async Task PersistFailedAsync(
        NeedsLlmReplyEvent request,
        string runId,
        string errorCode,
        string errorSummary)
    {
        await PersistDomainEventAsync(new AgentRunFailedEvent
        {
            RunId = runId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });

        await ScheduleTerminalCleanupAsync(runId);
    }

    private async Task FailAfterUnexpectedExceptionAsync(NeedsLlmReplyEvent request, string runId, Exception ex)
    {
        const string errorCode = "agent_run_unhandled_exception";
        var errorSummary = ex.Message;
        _logger.LogError(
            ex,
            "Agent run failed with unhandled exception: runId={RunId} correlation={CorrelationId}",
            runId,
            request.CorrelationId);

        if (request.Activity is not null && !string.IsNullOrWhiteSpace(request.TargetActorId))
        {
            try
            {
                await DispatchReadyEventAsync(
                    request,
                    "Sorry, I couldn't complete this reply. Please try again.",
                    null,
                    LlmReplyTerminalState.Failed,
                    errorCode,
                    errorSummary);
            }
            catch (AgentRunOutputDispatchException dispatchEx)
            {
                if (!await TryHandleOutputDispatchFailureAsync(request, runId, dispatchEx))
                    throw;
                return;
            }
        }

        await PersistFailedAsync(request, runId, errorCode, errorSummary);
    }

    private async Task DispatchReadyEventAsync(
        NeedsLlmReplyEvent request,
        string replyText,
        MessageContent? outboundIntent,
        LlmReplyTerminalState terminalState,
        string errorCode,
        string errorSummary)
    {
        if (string.IsNullOrWhiteSpace(request.TargetActorId))
            return;

        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = request.CorrelationId,
            RegistrationId = request.RegistrationId,
            SourceActorId = Id,
            Activity = request.Activity!.Clone(),
            Outbound = outboundIntent?.Clone() ?? new MessageContent { Text = replyText },
            TerminalState = terminalState,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            ReadyAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            // Echo the command-only relay credential straight back so ConversationGAgent's
            // outbound reply does not depend on its in-memory token dict still having the
            // entry. The actor consumes these fields and never persists them.
            ReplyToken = request.ReplyToken ?? string.Empty,
            ReplyTokenExpiresAtUnixMs = request.ReplyTokenExpiresAtUnixMs,
        };
        try
        {
            await SendToAsync(request.TargetActorId, ready, CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw new AgentRunOutputDispatchException(
                $"Failed to send LLM reply ready event to conversation actor '{request.TargetActorId}'.",
                ex);
        }
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

        await ApplyBotOwnerLlmConfigAsync(request, metadata, ct);

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
                "Failed to resolve bot owner scope id for LLM config: runId={RunId} correlation={CorrelationId} apiKeyId={ApiKeyId}",
                Id,
                request.CorrelationId,
                apiKeyId);
            return;
        }

        if (string.IsNullOrWhiteSpace(scopeId))
        {
            _logger.LogDebug(
                "No bot owner scope id resolved for LLM config: runId={RunId} correlation={CorrelationId} apiKeyId={ApiKeyId}",
                Id,
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
                "Applied bot owner LLM config: runId={RunId} correlation={CorrelationId} scopeId={ScopeId} model={Model} route={Route}",
                Id,
                request.CorrelationId,
                scopeId,
                string.IsNullOrWhiteSpace(config.DefaultModel) ? "<server-default>" : config.DefaultModel,
                string.IsNullOrWhiteSpace(config.PreferredLlmRoute) ? "<server-default>" : config.PreferredLlmRoute);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load bot owner LLM config: runId={RunId} correlation={CorrelationId} scopeId={ScopeId}",
                Id,
                request.CorrelationId,
                scopeId);
        }
    }

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

    private static bool CanNotifyDrop(NeedsLlmReplyEvent request) =>
        !string.IsNullOrWhiteSpace(request.TargetActorId) &&
        !string.IsNullOrWhiteSpace(request.CorrelationId);

    private async Task DispatchDropNotificationAsync(NeedsLlmReplyEvent request, string reason)
    {
        var dropped = new DeferredLlmReplyDroppedEvent
        {
            CorrelationId = request.CorrelationId,
            Reason = reason,
            DroppedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        };

        try
        {
            await SendToAsync(request.TargetActorId, dropped, CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw new AgentRunOutputDispatchException(
                $"Failed to send deferred LLM reply drop event to conversation actor '{request.TargetActorId}' (reason '{reason}').",
                ex);
        }
    }

    private async Task<bool> TryHandleOutputDispatchFailureAsync(
        NeedsLlmReplyEvent request,
        string runId,
        AgentRunOutputDispatchException ex)
    {
        _logger.LogWarning(
            ex,
            "Agent run output notification was not accepted; run remains retryable: runId={RunId} correlation={CorrelationId}",
            runId,
            request.CorrelationId);

        if (await TryScheduleStartRetryAsync(request, runId))
            return true;

        _logger.LogWarning(
            ex,
            "Agent run output retry could not be scheduled; propagating to runtime retry: runId={RunId} correlation={CorrelationId}",
            runId,
            request.CorrelationId);
        return false;
    }

    private async Task<bool> TryScheduleStartRetryAsync(NeedsLlmReplyEvent request, string runId)
    {
        if (Services.GetService<IActorRuntimeCallbackScheduler>() is null)
            return false;

        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                BuildOutputDispatchRetryCallbackId(runId),
                OutputDispatchRetryDelay,
                new AgentRunStartRequested
                {
                    Request = request.Clone(),
                },
                ct: CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to schedule agent run output retry: runId={RunId} actorId={ActorId}",
                runId,
                Id);
            return false;
        }
    }

    private async Task ScheduleTerminalCleanupAsync(string runId)
    {
        if (Services.GetService<IActorRuntimeCallbackScheduler>() is null)
            return;

        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                BuildCleanupCallbackId(runId),
                TerminalCleanupDelay,
                new AgentRunCleanupRequested
                {
                    RunId = runId,
                    RequestedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                },
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to schedule terminal agent run cleanup: runId={RunId} actorId={ActorId}",
                runId,
                Id);
        }
    }

    private static string BuildCleanupCallbackId(string runId)
    {
        var normalized = NormalizeOptional(runId) ?? "unknown";
        var chars = normalized
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
            .Take(96)
            .ToArray();
        return $"{TerminalCleanupCallbackPrefix}:{new string(chars)}";
    }

    private static string BuildOutputDispatchRetryCallbackId(string runId)
    {
        var normalized = NormalizeOptional(runId) ?? "unknown";
        var chars = normalized
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
            .Take(96)
            .ToArray();
        return $"{OutputDispatchRetryCallbackPrefix}:{new string(chars)}";
    }

    private async Task EnsureTargetActorAsync(string targetActorId)
    {
        if (string.IsNullOrWhiteSpace(targetActorId))
            return;

        var actor = await _actorRuntime.GetAsync(targetActorId);
        if (actor is null)
            await _actorRuntime.CreateAsync<ConversationGAgent>(targetActorId, CancellationToken.None);
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

    private static AgentRunGAgentState ApplyStarted(AgentRunGAgentState current, AgentRunStartedEvent evt)
    {
        var next = current.Clone();
        next.RunId = evt.RunId;
        next.CorrelationId = evt.CorrelationId;
        next.TargetActorId = evt.TargetActorId;
        next.Status = AgentRunStatus.Started;
        next.StartedAtUnixMs = evt.StartedAtUnixMs;
        return next;
    }

    private static AgentRunGAgentState ApplyReplyProduced(
        AgentRunGAgentState current,
        AgentRunReplyProducedEvent evt)
    {
        var next = current.Clone();
        next.RunId = string.IsNullOrWhiteSpace(next.RunId) ? evt.RunId : next.RunId;
        next.CorrelationId = string.IsNullOrWhiteSpace(next.CorrelationId) ? evt.CorrelationId : next.CorrelationId;
        next.TargetActorId = string.IsNullOrWhiteSpace(next.TargetActorId) ? evt.TargetActorId : next.TargetActorId;
        next.Status = AgentRunStatus.ReplyProduced;
        next.CompletedAtUnixMs = evt.ProducedAtUnixMs;
        next.ErrorCode = evt.ErrorCode;
        next.ErrorSummary = evt.ErrorSummary;
        return next;
    }

    private static AgentRunGAgentState ApplyDropped(AgentRunGAgentState current, AgentRunDroppedEvent evt)
    {
        var next = current.Clone();
        next.RunId = string.IsNullOrWhiteSpace(next.RunId) ? evt.RunId : next.RunId;
        next.CorrelationId = string.IsNullOrWhiteSpace(next.CorrelationId) ? evt.CorrelationId : next.CorrelationId;
        next.TargetActorId = string.IsNullOrWhiteSpace(next.TargetActorId) ? evt.TargetActorId : next.TargetActorId;
        next.Status = AgentRunStatus.Dropped;
        next.CompletedAtUnixMs = evt.DroppedAtUnixMs;
        next.ErrorCode = evt.Reason;
        next.ErrorSummary = string.Empty;
        return next;
    }

    private static AgentRunGAgentState ApplyFailed(AgentRunGAgentState current, AgentRunFailedEvent evt)
    {
        var next = current.Clone();
        next.RunId = string.IsNullOrWhiteSpace(next.RunId) ? evt.RunId : next.RunId;
        next.CorrelationId = string.IsNullOrWhiteSpace(next.CorrelationId) ? evt.CorrelationId : next.CorrelationId;
        next.TargetActorId = string.IsNullOrWhiteSpace(next.TargetActorId) ? evt.TargetActorId : next.TargetActorId;
        next.Status = AgentRunStatus.Failed;
        next.CompletedAtUnixMs = evt.FailedAtUnixMs;
        next.ErrorCode = evt.ErrorCode;
        next.ErrorSummary = evt.ErrorSummary;
        return next;
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private sealed class AgentRunOutputDispatchException(string message, Exception innerException)
        : Exception(message, innerException);
}
