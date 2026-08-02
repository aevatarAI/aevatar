namespace Aevatar.GAgentService.Projection.Contexts;

public sealed class AgentProfileCatalogProjectionContext : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}

public sealed class AgentProfileCurrentStateProjectionContext : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}
