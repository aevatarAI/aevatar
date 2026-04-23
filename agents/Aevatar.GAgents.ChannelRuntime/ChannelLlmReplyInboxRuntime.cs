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
    private readonly ILogger<ChannelLlmReplyInboxRuntime> _logger;
    private IAsyncDisposable? _subscription;

    public ChannelLlmReplyInboxRuntime(
        IStreamProvider streamProvider,
        IActorRuntime actorRuntime,
        IConversationReplyGenerator replyGenerator,
        ILogger<ChannelLlmReplyInboxRuntime> logger)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _replyGenerator = replyGenerator ?? throw new ArgumentNullException(nameof(replyGenerator));
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

    private async Task ProcessAsync(NeedsLlmReplyEvent request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Activity is null || string.IsNullOrWhiteSpace(request.TargetActorId))
        {
            _logger.LogWarning(
                "Dropping malformed deferred LLM reply request: correlation={CorrelationId}, target={TargetActorId}",
                request.CorrelationId,
                request.TargetActorId);
            return;
        }

        string replyText;
        var terminalState = LlmReplyTerminalState.Completed;
        var errorCode = string.Empty;
        var errorSummary = string.Empty;

        try
        {
            var effectiveMetadata = BuildEffectiveMetadata(request);
            replyText = await _replyGenerator.GenerateReplyAsync(
                request.Activity,
                effectiveMetadata,
                CancellationToken.None) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(replyText))
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
            Outbound = new MessageContent { Text = replyText },
            TerminalState = terminalState,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            ReadyAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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
