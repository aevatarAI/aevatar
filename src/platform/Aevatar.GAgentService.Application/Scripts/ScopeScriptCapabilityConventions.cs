namespace Aevatar.GAgentService.Application.Scripts;

internal static class ScopeScriptCapabilityConventions
{
    public static string NormalizeScriptId(string scriptId)
    {
        var normalized = ScopeScriptCapabilityOptions.NormalizeRequired(scriptId, nameof(scriptId));
        if (normalized.Contains(':', StringComparison.Ordinal))
            throw new InvalidOperationException("scriptId must not contain ':'.");

        return normalized;
    }

    public static string ResolveRevisionId(string? revisionId)
    {
        var normalized = NormalizeOptional(revisionId);
        return !string.IsNullOrWhiteSpace(normalized)
            ? normalized
            : $"rev-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
    }

    public static string ResolveExpectedBaseRevision(string? expectedBaseRevision) =>
        NormalizeOptional(expectedBaseRevision);

    public static string NormalizeOptional(string? value) => value?.Trim() ?? string.Empty;
}
