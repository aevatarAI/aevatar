using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.GAgents.Scheduled;

public sealed class UserAgentCatalogMaterializationContext : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }
}
