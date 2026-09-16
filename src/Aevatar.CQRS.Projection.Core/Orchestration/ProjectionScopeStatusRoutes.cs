namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Deterministic actor-id construction for the two status writers of one source projection
/// scope. Ids are built, never parsed: the source scope id is an opaque input.
/// </summary>
internal static class ProjectionScopeStatusRoutes
{
    public static string BuildLegacyActorId(string sourceScopeActorId) =>
        ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            sourceScopeActorId,
            ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
            ProjectionRuntimeMode.DurableMaterialization));

    public static string BuildTerminalActorId(string sourceScopeActorId) =>
        ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            sourceScopeActorId,
            ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
            ProjectionRuntimeMode.DurableMaterialization));

    public static ProjectionScopeStartRequest BuildTerminalStartRequest(string sourceScopeActorId) =>
        new()
        {
            RootActorId = sourceScopeActorId,
            ProjectionKind = ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
            Mode = ProjectionRuntimeMode.DurableMaterialization,
        };
}
