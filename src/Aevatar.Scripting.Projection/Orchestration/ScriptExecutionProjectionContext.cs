using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptExecutionProjectionContext
    : IProjectionSessionContext
{
    public required string SessionId { get; init; }

    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}
