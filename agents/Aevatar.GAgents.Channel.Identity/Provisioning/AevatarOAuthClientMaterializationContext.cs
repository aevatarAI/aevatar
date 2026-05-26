using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

public sealed class AevatarOAuthClientMaterializationContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }
}
