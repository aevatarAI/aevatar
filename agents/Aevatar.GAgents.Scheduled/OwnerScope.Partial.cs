namespace Aevatar.GAgents.ChannelRuntime;

public sealed partial class OwnerScope
{
    /// <summary>
    /// Canonical platform value for native NyxID surfaces (cli + web). For non-native
    /// surfaces (lark, telegram, …) the platform field carries the surface-specific
    /// canonical string set at the resolver edge.
    /// </summary>
    public const string NyxIdPlatform = "nyxid";

    /// <summary>
    /// Closed canonical set of platform values. Every <see cref="OwnerScope"/> at command-
    /// handler / resolver-output ingress must carry one of these — anything else is a
    /// resolver bug or a hand-constructed scope that bypassed the factory normalization.
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> CanonicalPlatforms =
        new(System.StringComparer.Ordinal) { "nyxid", "lark", "telegram" };

    /// <summary>
    /// Validates that the scope is well-formed at the command-handler / resolver-output
    /// boundary. Empty <c>nyx_user_id</c> or empty <c>platform</c> is rejected; the
    /// <c>platform</c> must be one of the canonical values (issue #466 §B). Non-native
    /// platforms additionally require <c>registration_scope_id</c> and <c>sender_id</c>.
    /// Returns <c>true</c> with no error message on success; otherwise sets
    /// <paramref name="error"/> to a human-readable reason.
    /// </summary>
    public bool TryValidate(out string? error)
    {
        if (string.IsNullOrWhiteSpace(NyxUserId))
        {
            error = "OwnerScope.nyx_user_id is required";
            return false;
        }
        if (string.IsNullOrWhiteSpace(Platform))
        {
            error = "OwnerScope.platform is required (\"nyxid\" for native cli/web; \"lark\"/\"telegram\"/… for channel surfaces)";
            return false;
        }
        if (!CanonicalPlatforms.Contains(Platform))
        {
            error = $"OwnerScope.platform '{Platform}' is not in the canonical set ({{nyxid, lark, telegram}}). Resolvers must produce a canonical lower-case value.";
            return false;
        }

        if (!IsNyxIdNative)
        {
            if (string.IsNullOrEmpty(RegistrationScopeId))
            {
                error = $"OwnerScope.registration_scope_id is required for platform=\"{Platform}\"";
                return false;
            }
            if (string.IsNullOrEmpty(SenderId))
            {
                error = $"OwnerScope.sender_id is required for platform=\"{Platform}\"";
                return false;
            }
        }

        error = null;
        return true;
    }

    public bool IsNyxIdNative => string.Equals(Platform, NyxIdPlatform, System.StringComparison.Ordinal);

    /// <summary>
    /// Strict full-tuple equality used at the readmodel filter boundary. Two scopes match
    /// iff every field is character-equal — except <c>Platform</c>, which is matched
    /// case-insensitively (defense-in-depth: factories always lowercase, but proto round-
    /// trips and hand-written tests can land non-canonical casing here).
    /// <c>null</c> on either side never matches.
    /// </summary>
    public bool MatchesStrictly(OwnerScope? other)
    {
        if (other is null) return false;
        return string.Equals(NyxUserId, other.NyxUserId, System.StringComparison.Ordinal)
               && string.Equals(Platform, other.Platform, System.StringComparison.OrdinalIgnoreCase)
               && string.Equals(RegistrationScopeId, other.RegistrationScopeId, System.StringComparison.Ordinal)
               && string.Equals(SenderId, other.SenderId, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Build the native NyxID-surface scope (cli + web). Empty registration / sender by contract.
    /// </summary>
    public static OwnerScope ForNyxIdNative(string nyxUserId) =>
        new()
        {
            NyxUserId = nyxUserId ?? string.Empty,
            Platform = NyxIdPlatform,
            RegistrationScopeId = string.Empty,
            SenderId = string.Empty,
        };

    /// <summary>
    /// Build a channel-surface scope. Per-sender (not per-conversation) — see
    /// ChannelUserConfigScope (issue #436) for the precedent.
    /// </summary>
    public static OwnerScope ForChannel(string nyxUserId, string platform, string registrationScopeId, string senderId) =>
        new()
        {
            NyxUserId = nyxUserId ?? string.Empty,
            Platform = (platform ?? string.Empty).Trim().ToLowerInvariant(),
            RegistrationScopeId = registrationScopeId ?? string.Empty,
            SenderId = senderId ?? string.Empty,
        };

    /// <summary>
    /// Lazy backfill: synthesize an OwnerScope from legacy scattered fields when the
    /// new <c>owner_scope</c> field is empty on a stored entry/document. Per the issue
    /// #466 migration plan, this only succeeds for the nyxid surface (cli/web): legacy
    /// lark agents lack <c>sender_id</c> and intentionally fall through (deprecate-and-
    /// recreate). Returns null when the legacy fields are insufficient.
    /// </summary>
    public static OwnerScope? FromLegacyFields(string? legacyOwnerNyxUserId, string? legacyPlatform)
    {
        var trimmedNyxUserId = legacyOwnerNyxUserId?.Trim();
        if (string.IsNullOrEmpty(trimmedNyxUserId))
            return null;

        var trimmedPlatform = legacyPlatform?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmedPlatform) || string.Equals(trimmedPlatform, NyxIdPlatform, System.StringComparison.Ordinal))
        {
            return ForNyxIdNative(trimmedNyxUserId);
        }

        // Channel-surface legacy data (lark/telegram) lacks sender_id, which is required
        // for strict full-tuple match. Rather than synthesize a partial scope that would
        // soft-match other senders on the same bot, fall through and force recreate.
        return null;
    }
}
