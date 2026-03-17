namespace Aevatar.GAgentService.Governance.Projection.Contexts;

public sealed class ServiceConfigurationProjectionContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}
