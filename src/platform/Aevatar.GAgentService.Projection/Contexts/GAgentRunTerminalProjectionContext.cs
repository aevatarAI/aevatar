using Aevatar.GAgentService.Abstractions.ScopeGAgents;

namespace Aevatar.GAgentService.Projection.Contexts;

public sealed class GAgentRunTerminalProjectionContext
    : IProjectionSessionScopedMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }

    public required string CorrelationId { get; init; }

    public required GAgentRunTerminalInteractionKind InteractionKind { get; init; }

    public string SessionId => CorrelationId;
}
