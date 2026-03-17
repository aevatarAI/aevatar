namespace Aevatar.GAgentService.Projection.Contexts;

public sealed class ServiceDeploymentCatalogProjectionContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}
