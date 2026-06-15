namespace Aevatar.AI.Abstractions.SkillInvocations;

public sealed class SkillInvocationTriggerOptions
{
    public const string CanonicalTriggerToken = "::";

    private readonly Dictionary<string, string[]> _tokensByPlatform = new(StringComparer.OrdinalIgnoreCase);

    public SkillInvocationTriggerOptions()
    {
        SetPlatformTokens("default", [CanonicalTriggerToken]);
        SetPlatformTokens("lark", [CanonicalTriggerToken, "/"]);
        SetPlatformTokens("web", [CanonicalTriggerToken, "/"]);
        SetPlatformTokens("cli", [CanonicalTriggerToken]);
    }

    public IReadOnlyList<string> GetTokens(string? platform)
    {
        if (!string.IsNullOrWhiteSpace(platform) &&
            _tokensByPlatform.TryGetValue(platform.Trim(), out var platformTokens))
        {
            return platformTokens;
        }

        return _tokensByPlatform["default"];
    }

    public void SetPlatformTokens(string platform, IEnumerable<string> tokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentNullException.ThrowIfNull(tokens);

        var normalized = tokens
            .Select(static token => token.Trim())
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static token => token.Length)
            .ThenBy(static token => token, StringComparer.Ordinal)
            .ToArray();

        _tokensByPlatform[platform.Trim()] = normalized.Length == 0
            ? [CanonicalTriggerToken]
            : normalized;
    }
}
