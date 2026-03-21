namespace Aevatar.CQRS.Projection.Core.Abstractions;

public readonly record struct ProjectionRuntimeScopeKey(
    string RootActorId,
    string ProjectionKind,
    ProjectionRuntimeMode Mode,
    string SessionId = "")
{
    public bool IsSession =>
        Mode == ProjectionRuntimeMode.SessionObservation;

    public bool IsDurable =>
        Mode == ProjectionRuntimeMode.DurableMaterialization;
}
