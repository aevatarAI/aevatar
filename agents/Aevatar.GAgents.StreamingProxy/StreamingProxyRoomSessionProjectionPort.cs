using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.StreamingProxy;

public sealed class StreamingProxyRoomSessionProjectionPort
    : EventSinkProjectionLifecyclePortBase<IStreamingProxyRoomSessionProjectionLease, StreamingProxyRoomSessionRuntimeLease, StreamingProxyRoomSessionEnvelope>,
      IStreamingProxyRoomSessionProjectionPort
{
    private readonly StreamingProxyCurrentStateProjectionPort _currentStateProjectionPort;

    public StreamingProxyRoomSessionProjectionPort(
        IProjectionScopeActivationService<StreamingProxyRoomSessionRuntimeLease> activationService,
        IProjectionScopeReleaseService<StreamingProxyRoomSessionRuntimeLease> releaseService,
        IProjectionSessionEventHub<StreamingProxyRoomSessionEnvelope> sessionEventHub,
        StreamingProxyCurrentStateProjectionPort currentStateProjectionPort)
        : base(
            static () => true,
            activationService,
            releaseService,
            sessionEventHub)
    {
        _currentStateProjectionPort = currentStateProjectionPort ??
                                      throw new ArgumentNullException(nameof(currentStateProjectionPort));
    }

    public async Task<IStreamingProxyRoomSessionProjectionLease?> EnsureRoomProjectionAsync(
        string actorId,
        string sessionId,
        CancellationToken ct = default)
    {
        return await EnsureChatProjectionAsync(actorId, sessionId, ct);
    }

    public async Task<IStreamingProxyRoomSessionProjectionLease?> EnsureChatProjectionAsync(
        string actorId,
        string sessionId,
        CancellationToken ct = default)
    {
        return await EnsureProjectionAsync(actorId, sessionId, StreamingProxyProjectionKinds.RoomChatSession, ct);
    }

    public async Task<IStreamingProxyRoomSessionProjectionLease?> EnsureSubscriptionProjectionAsync(
        string actorId,
        string subscriptionId,
        CancellationToken ct = default)
    {
        return await EnsureProjectionAsync(actorId, subscriptionId, StreamingProxyProjectionKinds.RoomSubscriptionSession, ct);
    }

    private async Task<IStreamingProxyRoomSessionProjectionLease?> EnsureProjectionAsync(
        string actorId,
        string sessionId,
        string projectionKind,
        CancellationToken ct)
    {
        await _currentStateProjectionPort.EnsureProjectionForActorAsync(actorId, ct);

        return await EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = projectionKind,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = sessionId,
            },
            ct);
    }
}
