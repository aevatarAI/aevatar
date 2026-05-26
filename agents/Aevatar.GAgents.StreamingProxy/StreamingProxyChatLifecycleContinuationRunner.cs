using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.StreamingProxy.Application.Rooms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.StreamingProxy;

// Refactor (iter104/cluster-1 r2):
//   Old pattern: StreamingProxyGAgent continuation handler awaited Nyx participant join/reply streaming inside the actor turn.
//   New principle: this host-side runner consumes typed continuation requests outside actor turns and returns observed outcomes through typed room commands.
internal sealed class StreamingProxyChatLifecycleContinuationRunner : IHostedService, IAsyncDisposable
{
    private readonly IStreamProvider _streamProvider;
    private readonly IActorEventSubscriptionProvider _subscriptionProvider;
    private readonly StreamingProxyNyxParticipantCoordinator _coordinator;
    private readonly IStreamingProxyRoomCommandService _roomCommandService;
    private readonly ILogger<StreamingProxyChatLifecycleContinuationRunner> _logger;
    private IAsyncDisposable? _subscription;

    public StreamingProxyChatLifecycleContinuationRunner(
        IStreamProvider streamProvider,
        IActorEventSubscriptionProvider subscriptionProvider,
        StreamingProxyNyxParticipantCoordinator coordinator,
        IStreamingProxyRoomCommandService roomCommandService,
        ILogger<StreamingProxyChatLifecycleContinuationRunner> logger)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _subscriptionProvider = subscriptionProvider ?? throw new ArgumentNullException(nameof(subscriptionProvider));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _roomCommandService = roomCommandService ?? throw new ArgumentNullException(nameof(roomCommandService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _streamProvider.GetStream(StreamingProxyGAgent.ChatLifecycleContinuationRunnerStreamId);
        _subscription = await _subscriptionProvider.SubscribeAsync<StreamingProxyChatLifecycleContinuationRequested>(
            StreamingProxyGAgent.ChatLifecycleContinuationRunnerStreamId,
            HandleAsync,
            cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (_subscription is null)
            return;

        await _subscription.DisposeAsync();
        _subscription = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscription is null)
            return;

        await _subscription.DisposeAsync();
        _subscription = null;
    }

    private Task HandleAsync(StreamingProxyChatLifecycleContinuationRequested request) =>
        RunAsync(request, CancellationToken.None);

    internal async Task RunAsync(
        StreamingProxyChatLifecycleContinuationRequested request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AccessToken))
            return;

        var roomId = request.RoomId?.Trim();
        if (string.IsNullOrWhiteSpace(roomId))
        {
            _logger.LogWarning("StreamingProxy chat lifecycle continuation ignored because room id is missing.");
            return;
        }

        var participants = await _coordinator.EnsureParticipantsJoinedAsync(
            request.ScopeId,
            roomId,
            request.AccessToken,
            ct,
            request.PreferredRoute,
            request.DefaultModel);

        var successfulReplies = participants.Count == 0
            ? 0
            : await _coordinator.GenerateRepliesAsync(
                participants,
                roomId,
                request.Prompt,
                request.SessionId,
                request.AccessToken,
                ct);

        var terminalState = StreamingProxyGAgent.DetermineParticipantTerminalState(successfulReplies);
        await _roomCommandService.PublishTerminalStateAsync(
            new StreamingProxyRoomTerminalStateCommand(
                roomId,
                request.SessionId,
                terminalState.Status,
                terminalState.ErrorMessage),
            ct);
    }
}
