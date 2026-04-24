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
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChannelLlmReplyInboxRuntime> _logger;
    private IAsyncDisposable? _subscription;

    public ChannelLlmReplyInboxRuntime(
        IStreamProvider streamProvider,
        IActorRuntime actorRuntime,
        IConversationReplyGenerator replyGenerator,
        IInteractiveReplyCollector? interactiveReplyCollector,
        NyxIdRelayOptions? relayOptions,
        ILogger<ChannelLlmReplyInboxRuntime> logger,
        TimeProvider? timeProvider = null)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _replyGenerator = replyGenerator ?? throw new ArgumentNullException(nameof(replyGenerator));
        _interactiveReplyCollector = interactiveReplyCollector;
        _relayOptions = relayOptions;
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
            var effectiveMetadata = BuildEffectiveMetadata(request);
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
