// ─────────────────────────────────────────────────────────────
// AgentId — Agent identifier utility
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Abstractions.Helpers;

/// <summary>
/// Agent ID normalization utility. Format: TypeShortName:RawId.
/// </summary>
public static class AgentId
{
    /// <summary>
    /// Generates normalized Agent ID based on Agent type and raw ID.
    /// </summary>
    public static string Normalize(Type agentType, string rawId)
    {
        var shortName = agentType.Name;
        // Remove common suffixes (longer suffix takes priority)
        if (shortName.EndsWith("GAgent", StringComparison.Ordinal))
            shortName = shortName[..^6];
        else if (shortName.EndsWith("Agent", StringComparison.Ordinal))
            shortName = shortName[..^5];

        return $"{shortName}:{rawId}";
    }

    /// <summary>
    /// Generates a new random Agent ID.
    /// </summary>
    public static string New(Type agentType) =>
        Normalize(agentType, Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// Extracts the raw ID portion from a normalized ID.
    /// </summary>
    public static string GetRawId(string normalizedId)
    {
        var idx = normalizedId.IndexOf(':');
        return idx >= 0 ? normalizedId[(idx + 1)..] : normalizedId;
    }
}