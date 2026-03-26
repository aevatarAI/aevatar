namespace Aevatar.GroupChat.Projection.Contexts;

public sealed class SourceCatalogProjectionContext : IProjectionMaterializationContext
{
    public string RootActorId { get; init; } = string.Empty;

    public string ProjectionKind { get; init; } = string.Empty;
}
