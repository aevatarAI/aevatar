namespace Aevatar.Studio.Workspace;

public static class StudioWorkspaceConventions
{
    public const string ProjectionKindValue = "studio-workspace";

    public static string BuildActorId(string scopeId) =>
        $"studio-workspace:{NormalizeScopeId(scopeId)}";

    public static string NormalizeScopeId(string scopeId)
    {
        var normalized = scopeId?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new ArgumentException("scopeId is required.", nameof(scopeId));
        return normalized;
    }
}
