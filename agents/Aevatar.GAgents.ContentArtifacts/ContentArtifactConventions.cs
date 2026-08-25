using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aevatar.GAgents.ContentArtifacts;

public static class ContentArtifactConventions
{
    public const string ActorIdPrefix = "content-artifact";
    public const string PinActorIdPrefix = "content-artifact-pin";
    public const int MaxInlineContentBytes = 64 * 1024;
    public const int MaxLabelCount = 8;
    public const int MaxLabelValueCharacters = 256;
    private const string ReservedLabelPrefix = "aevatar.";
    private static readonly Regex LabelKeyPattern = new(
        "^[a-z0-9]([a-z0-9._-]{0,62}[a-z0-9])?$",
        RegexOptions.CultureInvariant);

    public static string BuildArtifactId(string scopeId, string dedupKey)
    {
        var normalizedScopeId = NormalizeScopeId(scopeId);
        var normalizedDedupKey = NormalizeRequired(dedupKey, nameof(dedupKey));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{normalizedScopeId}\n{normalizedDedupKey}"));
        return $"artifact-{Convert.ToHexStringLower(digest)}";
    }

    public static string BuildActorId(string scopeId, string artifactId) =>
        $"{ActorIdPrefix}:{NormalizeScopeId(scopeId)}:{NormalizeArtifactId(artifactId)}";

    public static string BuildPinActorId(string scopeId, string pinKey) =>
        $"{PinActorIdPrefix}:{NormalizeScopeId(scopeId)}:{NormalizeLabelKey(pinKey, nameof(pinKey))}";

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

    public static string NormalizeLabelKey(string? key, string parameterName)
    {
        var normalized = NormalizeRequired(key, parameterName);
        if (!LabelKeyPattern.IsMatch(normalized))
        {
            throw new ArgumentException(
                $"{parameterName} must match [a-z0-9]([a-z0-9._-]{{0,62}}[a-z0-9])?.",
                parameterName);
        }
        if (normalized.StartsWith(ReservedLabelPrefix, StringComparison.Ordinal))
            throw new ArgumentException($"{parameterName} must not use the reserved 'aevatar.' prefix.", parameterName);
        return normalized;
    }

    public static string NormalizeLabelValue(string? value, string parameterName)
    {
        var normalized = NormalizeRequired(value, parameterName);
        if (normalized.Contains('\r') || normalized.Contains('\n'))
            throw new ArgumentException($"{parameterName} must be a single line.", parameterName);
        if (normalized.EnumerateRunes().Take(MaxLabelValueCharacters + 1).Count() > MaxLabelValueCharacters)
        {
            throw new ArgumentException(
                $"{parameterName} must be at most {MaxLabelValueCharacters} characters.",
                parameterName);
        }
        return normalized;
    }

    // Implement (issue #3527):
    //   Behavior: labels are bounded, canonical creation facts and pin keys share their key grammar.
    //   Why this shape: typed validation keeps query paths stable without introducing a metadata bag.
    public static IReadOnlyDictionary<string, string> NormalizeLabels(
        IReadOnlyDictionary<string, string>? labels)
    {
        if (labels == null || labels.Count == 0)
            return new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (labels.Count > MaxLabelCount)
            throw new ArgumentException($"labels must contain at most {MaxLabelCount} entries.", nameof(labels));

        var normalized = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in labels)
        {
            normalized.Add(
                NormalizeLabelKey(key, "labels.key"),
                NormalizeLabelValue(value, $"labels['{key}']"));
        }
        return normalized;
    }

    public static void ValidateCanonicalLabels(IReadOnlyDictionary<string, string> labels)
    {
        var normalized = NormalizeLabels(labels);
        if (normalized.Count != labels.Count ||
            normalized.Any(pair => !labels.TryGetValue(pair.Key, out var value) ||
                                   !string.Equals(value, pair.Value, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("ContentArtifact labels must be canonical.");
        }
    }

    public static string NormalizeRequired(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        return normalized;
    }
}
