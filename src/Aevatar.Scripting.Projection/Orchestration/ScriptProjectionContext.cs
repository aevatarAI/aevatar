namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptProjectionContext : IProjectionContext
{
    public required string ProjectionId { get; init; }
    public required string RootActorId { get; init; }
    public required string ScriptId { get; init; }
}
