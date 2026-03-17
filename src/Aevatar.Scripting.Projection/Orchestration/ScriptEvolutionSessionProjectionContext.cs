namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionSessionProjectionContext
    : IProjectionSessionContext
{
    public required string SessionId { get; init; }
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }
}
