using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.GAgents.StreamingProxy;

public interface IStreamingProxyRoomSessionProjectionLease
{
    string ActorId { get; }

    string SessionId { get; }
}

public interface IStreamingProxyRoomSessionProjectionPort
    : IEventSinkProjectionLifecyclePort<IStreamingProxyRoomSessionProjectionLease, StreamingProxyRoomSessionEnvelope>
{
    Task<IStreamingProxyRoomSessionProjectionLease?> EnsureRoomProjectionAsync(
        string actorId,
        string sessionId,
        CancellationToken ct = default);
}
