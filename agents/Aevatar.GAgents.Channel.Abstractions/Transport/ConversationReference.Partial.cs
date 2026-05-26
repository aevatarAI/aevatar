namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Helpers for deterministic conversation reference construction.
/// </summary>
public sealed partial class ConversationReference
{
    /// <summary>
    /// Creates one normalized conversation reference from deterministic canonical-key segments.
    /// </summary>
    /// <remarks>
    /// Callers must provide at least one deterministic segment beyond the channel id so the canonical key remains stable
    /// for routing and deduplication.
    /// </remarks>
    public static ConversationReference Create(
        ChannelId channel,
        BotInstanceId bot,
        ConversationScope scope,
        string? partition,
        params string[] canonicalSegments) => new()
    {
        Channel = channel?.Clone() ?? throw new ArgumentNullException(nameof(channel)),
        Bot = bot?.Clone() ?? throw new ArgumentNullException(nameof(bot)),
        Scope = scope,
        Partition = NormalizeOptional(partition),
        CanonicalKey = BuildCanonicalKey(channel, canonicalSegments),
    };

    /// <summary>
    /// Builds one deterministic canonical key by prefixing the normalized channel id and joining each segment with <c>:</c>.
    /// </summary>
    [CanonicalKeyGenerator]
    public static string BuildCanonicalKey(ChannelId channel, params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Length == 0)
        {
            throw new ArgumentException(
                "Canonical key must include at least one deterministic segment beyond the channel id.",
                nameof(segments));
        }

        var normalized = new List<string> { NormalizeSegment(channel.Value, nameof(channel)) };
        for (var i = 0; i < segments.Length; i++)
        {
            normalized.Add(NormalizeSegment(segments[i], $"segments[{i}]"));
        }

        return string.Join(':', normalized);
    }

    private static string NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeSegment(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Canonical key segment cannot be empty.", paramName);

        var normalized = value.Trim();
        if (normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Canonical key segments must not contain ':'. Supply each deterministic segment separately.",
                paramName);
        }

        return normalized;
    }
}
