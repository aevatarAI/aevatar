using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgents.ContentArtifacts;

public static class ContentArtifactConventions
{
    public const string ActorIdPrefix = "content-artifact";
    public const int MaxInlineContentBytes = 64 * 1024;

    public static string BuildArtifactId(string scopeId, string dedupKey)
    {
        var normalizedScopeId = NormalizeScopeId(scopeId);
        var normalizedDedupKey = NormalizeRequired(dedupKey, nameof(dedupKey));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{normalizedScopeId}\n{normalizedDedupKey}"));
        return $"artifact-{Convert.ToHexStringLower(digest)}";
    }

    public static string BuildActorId(string scopeId, string artifactId) =>
        $"{ActorIdPrefix}:{NormalizeScopeId(scopeId)}:{NormalizeArtifactId(artifactId)}";

    public static string BuildRevisionId(string artifactId, long revisionNumber)
    {
        if (revisionNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(revisionNumber), "revisionNumber must be positive.");
        return $"{NormalizeArtifactId(artifactId)}-revision-{revisionNumber}";
    }

    public static string NormalizeScopeId(string? scopeId)
    {
        var normalized = NormalizeRequired(scopeId, nameof(scopeId));
        if (normalized.Contains(':'))
            throw new ArgumentException("scopeId must not contain ':' (it is the actor-id separator).", nameof(scopeId));
        return normalized;
    }

    public static string NormalizeArtifactId(string? artifactId)
    {
        var normalized = NormalizeRequired(artifactId, nameof(artifactId));
        if (normalized.Contains(':'))
            throw new ArgumentException("artifactId must not contain ':' (it is the actor-id separator).", nameof(artifactId));
        return normalized;
    }

    public static string NormalizeRequired(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        return normalized;
    }
}
