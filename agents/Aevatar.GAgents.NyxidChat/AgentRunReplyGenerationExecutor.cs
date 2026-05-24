using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public sealed class AgentRunReplyGenerationExecutor : IAgentRunReplyGenerationExecutorPort
{
    private const string PublisherActorId = "agent-run-reply-generation-executor";
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly IConversationReplyGenerator _replyGenerator;
    private readonly IInteractiveReplyCollector? _interactiveReplyCollector;
    private readonly Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? _relayOptions;
    private readonly INyxIdRelayScopeResolver? _scopeResolver;
    private readonly IUserConfigQueryPort? _userConfigQueryPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentRunReplyGenerationExecutor> _logger;

    public AgentRunReplyGenerationExecutor(
        IActorDispatchPort actorDispatchPort,
        IConversationReplyGenerator replyGenerator,
        IInteractiveReplyCollector? interactiveReplyCollector,
        Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? relayOptions,
        ILogger<AgentRunReplyGenerationExecutor> logger,
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

    public Task StartAsync(AgentRunReplyGenerationExecutionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var workItem = request with { Request = request.Request.Clone() };
        _ = Task.Run(() => ExecuteAndReportAsync(workItem), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task ExecuteAndReportAsync(AgentRunReplyGenerationExecutionRequest workItem)
    {
        try
        {
            var completed = await ExecuteAsync(workItem).ConfigureAwait(false);
            await DispatchToRunActorAsync(workItem.RunActorId, completed, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Agent run reply generation executor failed before completion: runId={RunId} correlation={CorrelationId}",
                workItem.RunId,
                workItem.Request.CorrelationId);
            var failed = new AgentRunReplyGenerationFailed
            {
                RunId = workItem.RunId,
                CorrelationId = workItem.Request.CorrelationId,
                TargetActorId = workItem.Request.TargetActorId,
                ErrorCode = "agent_run_generation_executor_failed",
                ErrorSummary = ex.Message,
                FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                Attempt = workItem.Attempt,
                Request = workItem.Request.Clone(),
            };
            try
            {
                await DispatchToRunActorAsync(workItem.RunActorId, failed, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception dispatchEx)
            {
                _logger.LogError(
                    dispatchEx,
                    "Failed to dispatch agent run generation failure command: runId={RunId} actorId={ActorId}",
                    workItem.RunId,
                    workItem.RunActorId);
            }
        }
    }

    internal async Task<AgentRunReplyGenerationCompleted> ExecuteAsync(
        AgentRunReplyGenerationExecutionRequest workItem)
    {
        var request = workItem.Request.Clone();
        string replyText;
        MessageContent? outboundIntent = null;
        var terminalState = LlmReplyTerminalState.Completed;
        var errorCode = string.Empty;
        var errorSummary = string.Empty;
        using TurnStreamingReplySink? streamingSink = TryBuildStreamingSink(request, request.TargetActorId);
        var streamingState = TryBuildStreamingReplyState(streamingSink);

        ReplyGenerationContext generationContext;
        using (var metadataCts = new CancellationTokenSource(AgentRunGAgent.MetadataBuildBudget))
        {
            try
            {
                generationContext = await BuildGenerationContextAsync(request, metadataCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (metadataCts.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "Deferred LLM reply metadata build timed out after {TimeoutSeconds}s: runId={RunId} correlation={CorrelationId}",
                    (int)AgentRunGAgent.MetadataBuildBudget.TotalSeconds,
                    workItem.RunId,
                    request.CorrelationId);
                replyText = "Sorry, I couldn't load your model preferences in time. Please try again.";
                terminalState = LlmReplyTerminalState.Failed;
                errorCode = "llm_reply_metadata_timeout";
                errorSummary =
                    $"Metadata enrichment exceeded {(int)AgentRunGAgent.MetadataBuildBudget.TotalSeconds}s budget.";
                await FinalizeFailureStreamingSinkAsync(streamingState, replyText, outboundIntent)
                    .ConfigureAwait(false);
                return BuildCompleted(workItem, request, replyText, outboundIntent, terminalState, errorCode, errorSummary);
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

                var replyResult = _replyGenerator is ITypedConversationReplyGenerator typedReplyGenerator
                    ? await typedReplyGenerator.GenerateReplyAsync(
                            request.Activity!,
                            generationContext.Metadata,
                            generationContext.LlmControl,
                            generationContext.ToolContext,
                            streamingState,
                            timeoutCts.Token)
                        .ConfigureAwait(false)
                    : await _replyGenerator.GenerateReplyAsync(
                            request.Activity!,
                            generationContext.Metadata,
                            streamingState,
                            timeoutCts.Token)
                        .ConfigureAwait(false);
                replyText = replyResult.Text ?? string.Empty;
                if (replyResult.Usage is not null || !string.IsNullOrEmpty(replyResult.FinishReason))
                {
                    _logger.LogInformation(
                        "LLM reply closeout: runId={RunId} correlation={CorrelationId} promptTokens={Prompt} completionTokens={Completion} totalTokens={Total} finishReason={FinishReason}",
                        workItem.RunId,
                        request.CorrelationId,
                        replyResult.Usage?.PromptTokens,
                        replyResult.Usage?.CompletionTokens,
                        replyResult.Usage?.TotalTokens,
                        replyResult.FinishReason ?? "(none)");
                }

                outboundIntent = _interactiveReplyCollector?.TryTake();
            }
            finally
            {
                interactiveReplyScope?.Dispose();
            }

            if (streamingState is not null &&
                outboundIntent is null &&
                !string.IsNullOrWhiteSpace(replyText))
            {
                await streamingState.FinalizeAsync(replyText, CancellationToken.None)
                    .ConfigureAwait(false);
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
                workItem.RunId,
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
                workItem.RunId,
                request.CorrelationId);
        }

        if (terminalState == LlmReplyTerminalState.Failed)
        {
            await FinalizeFailureStreamingSinkAsync(streamingState, replyText, outboundIntent)
                .ConfigureAwait(false);
        }

        return BuildCompleted(workItem, request, replyText, outboundIntent, terminalState, errorCode, errorSummary);
    }

    private AgentRunReplyGenerationCompleted BuildCompleted(
        AgentRunReplyGenerationExecutionRequest workItem,
        NeedsLlmReplyEvent request,
        string replyText,
        MessageContent? outboundIntent,
        LlmReplyTerminalState terminalState,
        string errorCode,
        string errorSummary)
    {
        var completed = new AgentRunReplyGenerationCompleted
        {
            RunId = workItem.RunId,
            CorrelationId = request.CorrelationId,
            TargetActorId = request.TargetActorId,
            ReplyText = replyText ?? string.Empty,
            TerminalState = terminalState,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            CompletedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            Attempt = workItem.Attempt,
            Request = request.Clone(),
        };
        if (outboundIntent is not null)
            completed.Outbound = outboundIntent.Clone();
        return completed;
    }

    private async Task DispatchToRunActorAsync<TCommand>(
        string runActorId,
        TCommand command,
        CancellationToken ct)
        where TCommand : IMessage
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, runActorId),
        };
        await _actorDispatchPort.DispatchAsync(runActorId, envelope, ct).ConfigureAwait(false);
    }

    private async Task FinalizeFailureStreamingSinkAsync(
        StreamingReplyRunState? streamingState,
        string replyText,
        MessageContent? outboundIntent)
    {
        if (streamingState is not null &&
            outboundIntent is null &&
            !string.IsNullOrWhiteSpace(replyText))
        {
            try
            {
                await streamingState.FinalizeAsync(replyText, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to finalize streaming failure text for agent run");
            }
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
        return new TurnStreamingReplySink(
            _actorDispatchPort,
            targetActorId,
            request.CorrelationId,
            request.RegistrationId,
            request.Activity.Clone(),
            request.ReplyToken,
            request.ReplyTokenExpiresAtUnixMs,
            _timeProvider,
            _logger,
            cardMode);
    }

    private StreamingReplyRunState? TryBuildStreamingReplyState(TurnStreamingReplySink? sink)
    {
        if (sink is null || _relayOptions is null)
            return null;

        var cardMode = _relayOptions.StreamingCardKitEnabled;
        var throttle = TimeSpan.FromMilliseconds(Math.Max(0, cardMode
            ? _relayOptions.StreamingCardKitFlushIntervalMs
            : _relayOptions.StreamingFlushIntervalMs));
        var maxInterimChunks = cardMode
            ? int.MaxValue
            : Math.Max(0, _relayOptions.StreamingMaxInterimChunks);

        return new StreamingReplyRunState(sink, throttle, maxInterimChunks, _timeProvider);
    }

    private sealed record ReplyGenerationContext(
        IReadOnlyDictionary<string, string> Metadata,
        LLMControlContext LlmControl,
        AgentToolExecutionContext ToolContext);

    private async Task<ReplyGenerationContext> BuildGenerationContextAsync(
        NeedsLlmReplyEvent request,
        CancellationToken ct)
    {
        var routedModel = NormalizeOptional(request.TargetRef?.ForwardToModel?.ModelName);
        var metadata = new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal);

        var control = LLMControlContextMapper.FromPayload(request.LlmControl);
        control = await ApplyBotOwnerLlmConfigAsync(request, control, ct).ConfigureAwait(false);
        if (routedModel is not null)
            control = control with { ModelOverride = routedModel };

        var userAccessToken = request.Activity?.TransportExtras?.NyxUserAccessToken?.Trim();
        if (!string.IsNullOrWhiteSpace(userAccessToken))
        {
            control = control with
            {
                NyxIdAccessToken = userAccessToken,
                NyxIdOrgToken = userAccessToken,
            };
        }

        return new ReplyGenerationContext(
            metadata,
            control,
            AgentToolExecutionContextMapper.FromPayload(request.ToolContext));
    }

    private async Task<LLMControlContext> ApplyBotOwnerLlmConfigAsync(
        NeedsLlmReplyEvent request,
        LLMControlContext control,
        CancellationToken ct)
    {
        if (_scopeResolver is null || _userConfigQueryPort is null)
            return control;

        var apiKeyId = request.Activity?.Bot?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(apiKeyId))
            return control;

        string? scopeId;
        try
        {
            scopeId = await _scopeResolver.ResolveScopeIdByApiKeyAsync(apiKeyId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve bot owner scope id for LLM config: correlation={CorrelationId} apiKeyId={ApiKeyId}",
                request.CorrelationId,
                apiKeyId);
            return control;
        }

        if (string.IsNullOrWhiteSpace(scopeId))
        {
            _logger.LogDebug(
                "No bot owner scope id resolved for LLM config: correlation={CorrelationId} apiKeyId={ApiKeyId}",
                request.CorrelationId,
                apiKeyId);
            return control;
        }

        try
        {
            var config = await _userConfigQueryPort.GetAsync(scopeId, ct).ConfigureAwait(false);
            control = control with
            {
                ModelOverride = string.IsNullOrWhiteSpace(config.DefaultModel)
                    ? control.ModelOverride
                    : config.DefaultModel.Trim(),
                NyxIdRoutePreference = string.IsNullOrWhiteSpace(config.PreferredLlmRoute)
                    ? control.NyxIdRoutePreference
                    : config.PreferredLlmRoute.Trim(),
                MaxToolRoundsOverride = config.MaxToolRounds > 0
                    ? config.MaxToolRounds
                    : control.MaxToolRoundsOverride,
            };

            _logger.LogInformation(
                "Applied bot owner LLM config: correlation={CorrelationId} scopeId={ScopeId} model={Model} route={Route}",
                request.CorrelationId,
                scopeId,
                string.IsNullOrWhiteSpace(config.DefaultModel) ? "<server-default>" : config.DefaultModel,
                string.IsNullOrWhiteSpace(config.PreferredLlmRoute) ? "<server-default>" : config.PreferredLlmRoute);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load bot owner LLM config: correlation={CorrelationId} scopeId={ScopeId}",
                request.CorrelationId,
                scopeId);
        }

        return control;
    }

    private TimeSpan ResolveFallbackTimeout()
    {
        if (_relayOptions is null)
            return TimeSpan.FromSeconds(AgentRunGAgent.FallbackTimeoutSecondsDefault);
        var configured = _relayOptions.ResponseTimeoutSeconds;
        if (configured <= 0)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds(configured);
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

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private sealed class StreamingReplyRunState : IStreamingReplySink
    {
        private readonly TurnStreamingReplySink _sink;
        private readonly TimeSpan _throttle;
        private readonly int _maxInterimChunks;
        private readonly TimeProvider _timeProvider;
        private string _lastEmittedText = string.Empty;
        private DateTimeOffset _lastEmitAt = DateTimeOffset.MinValue;
        private int _chunksEmitted;
        private string _pendingText = string.Empty;

        public StreamingReplyRunState(
            TurnStreamingReplySink sink,
            TimeSpan throttle,
            int maxInterimChunks,
            TimeProvider timeProvider)
        {
            _sink = sink;
            _throttle = throttle < TimeSpan.Zero ? TimeSpan.Zero : throttle;
            _maxInterimChunks = maxInterimChunks < 0 ? 0 : maxInterimChunks;
            _timeProvider = timeProvider;
        }

        public Task OnDeltaAsync(string accumulatedText, CancellationToken ct) =>
            TryDispatchAsync(accumulatedText, isFinal: false, ct);

        public Task FinalizeAsync(string finalText, CancellationToken ct) =>
            TryDispatchAsync(finalText, isFinal: true, ct);

        private async Task TryDispatchAsync(string text, bool isFinal, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (string.Equals(text, _lastEmittedText, StringComparison.Ordinal))
            {
                if (isFinal || string.Equals(text, _pendingText, StringComparison.Ordinal))
                    ClearPending();
                return;
            }

            if (!isFinal && _chunksEmitted >= _maxInterimChunks)
            {
                StashPending(text);
                return;
            }

            if (!isFinal)
            {
                var elapsed = _timeProvider.GetUtcNow() - _lastEmitAt;
                if (elapsed < _throttle)
                {
                    StashPending(text);
                    return;
                }
            }

            await _sink.DispatchAsync(text, ct).ConfigureAwait(false);
            if (_sink.ChunksEmitted > _chunksEmitted)
            {
                _lastEmittedText = text;
                _lastEmitAt = _timeProvider.GetUtcNow();
                _chunksEmitted = _sink.ChunksEmitted;
                if (isFinal || string.Equals(_pendingText, text, StringComparison.Ordinal))
                    ClearPending();
            }
        }

        private void StashPending(string text)
        {
            _pendingText = text;
        }

        private void ClearPending()
        {
            _pendingText = string.Empty;
        }
    }
}
