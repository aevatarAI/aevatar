namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionSessionProjectionContext
    : IProjectionSessionContext
{
    public string SessionId { get; set; } = string.Empty;
    public string RootActorId { get; set; } = string.Empty;
    public string ProjectionKind { get; set; } = string.Empty;
}
