using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.GAgents.StreamingProxy;

public sealed class StreamingProxyCurrentStateProjectionContext : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}
