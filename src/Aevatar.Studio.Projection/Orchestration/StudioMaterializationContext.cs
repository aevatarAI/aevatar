using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.Studio.Projection.Orchestration;

/// <summary>
/// Materialization context for Studio projection scopes.
/// Shared by all Studio current-state projectors (UserConfig, etc.).
/// </summary>
public sealed class StudioMaterializationContext : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}
