using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.StreamingProxy.Application.Rooms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.StreamingProxy;

internal sealed class StreamingProxyChatLifecycleContinuationRunner : IHostedService, IAsyncDisposable
{
    private readonly IStreamProvider _streamProvider;
    private readonly IActorEventSubscriptionProvider _subscriptionProvider;
    private readonly StreamingProxyNyxParticipantCoordinator _coordinator;
    private readonly IStreamingProxyRoomCommandService _roomCommandService;
    private readonly ILogger<StreamingProxyChatLifecycleContinuationRunner> _logger;
    private IAsyncDisposable? _subscription;
    private IAsyncDisposable? _replySubscription;

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
        _replySubscription = await _subscriptionProvider.SubscribeAsync<StreamingProxyChatParticipantReplyRequested>(
            StreamingProxyGAgent.ChatLifecycleContinuationRunnerStreamId,
            HandleReplyRequestAsync,
            cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
            _subscription = null;
        }

        if (_replySubscription is not null)
        {
            await _replySubscription.DisposeAsync();
            _replySubscription = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
            _subscription = null;
        }

        if (_replySubscription is not null)
        {
            await _replySubscription.DisposeAsync();
            _replySubscription = null;
        }
    }

    private Task HandleAsync(StreamingProxyChatLifecycleContinuationRequested request) =>
        RunAsync(request, CancellationToken.None);

    private Task HandleReplyRequestAsync(StreamingProxyChatParticipantReplyRequested request) =>
        RunParticipantReplyAsync(request, CancellationToken.None);

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

        var participants = await _coordinator.ResolveParticipantsAsync(
            request.ScopeId,
            roomId,
            request.AccessToken,
            ct,
            request.PreferredRoute,
            request.DefaultModel);

        foreach (var participant in participants)
        {
            await _roomCommandService.JoinAsync(
                new StreamingProxyRoomJoinCommand(
                    roomId,
                    participant.ParticipantId,
                    participant.DisplayName),
                ct);
        }

        await _roomCommandService.SubmitParticipantsResolvedAsync(
            new StreamingProxyRoomParticipantsResolvedCommand(
                roomId,
                request.SessionId,
                participants
                    .Select(participant => new StreamingProxyChatLifecycleParticipant
                    {
                        ParticipantId = participant.ParticipantId,
                        DisplayName = participant.DisplayName,
                        RoutePreference = participant.RoutePreference,
                        Model = participant.Model ?? string.Empty,
                        Status = StreamingProxyChatLifecycleParticipantStatus.Active,
                    })
                    .ToList()),
            ct);
    }

    internal async Task RunParticipantReplyAsync(
        StreamingProxyChatParticipantReplyRequested request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RoomId) ||
            string.IsNullOrWhiteSpace(request.SessionId) ||
            string.IsNullOrWhiteSpace(request.ParticipantId))
        {
            return;
        }

        var outcome = await _coordinator.GenerateReplyAsync(request, ct);
        if (outcome.IsSuccess)
        {
            await _roomCommandService.SubmitParticipantReplyObservedAsync(
                new StreamingProxyRoomParticipantReplyObservedCommand(
                    request.RoomId,
                    request.SessionId,
                    request.ParticipantId,
                    request.Round,
                    request.ParticipantIndex,
                    outcome.Content ?? string.Empty),
                ct);
            return;
        }

        await _roomCommandService.SubmitParticipantReplyFailedAsync(
            new StreamingProxyRoomParticipantReplyFailedCommand(
                request.RoomId,
                request.SessionId,
                request.ParticipantId,
                request.Round,
                request.ParticipantIndex,
                outcome.FailureKind,
                outcome.ErrorMessage),
            ct);
    }
}
