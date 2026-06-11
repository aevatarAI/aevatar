using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.GAgentService.Projection.Orchestration;

/// <summary>
/// Actorized Projection Pipeline session context for a script service AGUI run.
/// It carries the root actor, run session id, and projection kind needed to
/// bind a session-scoped projector without process-local lookup state.
/// </summary>
public sealed class ScriptServiceAguiProjectionContext : IProjectionSessionContext
{
    public required string SessionId { get; init; }
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }
}
