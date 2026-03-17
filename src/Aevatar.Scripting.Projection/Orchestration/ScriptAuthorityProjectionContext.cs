namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptAuthorityProjectionContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}
