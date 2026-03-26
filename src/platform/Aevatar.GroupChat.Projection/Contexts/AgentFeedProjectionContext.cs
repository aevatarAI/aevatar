using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.GroupChat.Projection.Contexts;

public sealed class AgentFeedProjectionContext : IProjectionMaterializationContext
{
    public string RootActorId { get; init; } = string.Empty;

    public string ProjectionKind { get; init; } = string.Empty;
}
