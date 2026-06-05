using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.GAgentService.Projection.Contexts;

public sealed class ScheduledDispatchProjectionContext : IProjectionMaterializationContext
{
    public string RootActorId { get; init; } = string.Empty;

    public string ProjectionKind { get; init; } = string.Empty;
}
