using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class GAgentDraftRunProjectionContext : IProjectionSessionContext
{
    public required string SessionId { get; init; }
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }
}
