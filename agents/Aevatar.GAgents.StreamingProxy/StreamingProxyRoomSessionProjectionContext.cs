using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.GAgents.StreamingProxy;

public sealed class StreamingProxyRoomSessionProjectionContext : IProjectionSessionContext
{
    public required string SessionId { get; init; }

    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}
