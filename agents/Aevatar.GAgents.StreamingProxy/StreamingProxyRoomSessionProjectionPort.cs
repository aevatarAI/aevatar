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
        await _currentStateProjectionPort.EnsureProjectionForActorAsync(actorId, ct);

        return await EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = StreamingProxyProjectionKinds.RoomSession,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = sessionId,
            },
            ct);
    }
}
