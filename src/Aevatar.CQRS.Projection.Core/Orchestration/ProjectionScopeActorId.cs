namespace Aevatar.CQRS.Projection.Core.Orchestration;

public static class ProjectionScopeActorId
{
    public const string DurablePrefix = "projection.durable.scope";
    public const string SessionPrefix = "projection.session.scope";

    public static string Build(ProjectionRuntimeScopeKey scopeKey)
    {
        if (string.IsNullOrWhiteSpace(scopeKey.RootActorId))
            throw new ArgumentException("Root actor id is required.", nameof(scopeKey));
        if (string.IsNullOrWhiteSpace(scopeKey.ProjectionKind))
            throw new ArgumentException("Projection kind is required.", nameof(scopeKey));

        var prefix = scopeKey.Mode == ProjectionRuntimeMode.DurableMaterialization
            ? DurablePrefix
            : SessionPrefix;

        return string.IsNullOrWhiteSpace(scopeKey.SessionId)
            ? $"{prefix}:{scopeKey.ProjectionKind.Trim()}:{scopeKey.RootActorId.Trim()}"
            : $"{prefix}:{scopeKey.ProjectionKind.Trim()}:{scopeKey.RootActorId.Trim()}:{scopeKey.SessionId.Trim()}";
    }
}

internal static class ProjectionScopeModeMapper
{
    public static ProjectionScopeMode ToProto(ProjectionRuntimeMode mode) =>
        (ProjectionScopeMode)(mode == ProjectionRuntimeMode.DurableMaterialization ? 1 : 2);

    public static ProjectionRuntimeMode ToRuntime(ProjectionScopeMode mode) =>
        (int)mode == 1
            ? ProjectionRuntimeMode.DurableMaterialization
            : ProjectionRuntimeMode.SessionObservation;
}
