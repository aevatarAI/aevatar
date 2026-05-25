using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.GAgents.StatusDashboard;

public sealed class HealthProbeMaterializationContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }
}
