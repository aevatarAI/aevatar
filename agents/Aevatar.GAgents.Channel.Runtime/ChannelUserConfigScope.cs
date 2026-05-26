namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Composes the per-end-user scope id used by <c>UserConfigGAgent</c> for
/// channel-bound preferences such as the saved <c>github_username</c>.
///
/// The bot's <c>RegistrationScopeId</c> alone is per-NyxID-account (one bot =
/// one scope), so multiple Lark users sharing the same bot would otherwise
/// share a single user-config record and overwrite each other's preferences
/// (issue #436). The composite <c>{registrationScopeId}:{platform}:{senderId}</c>
/// gives every channel sender their own actor while leaving the bot scope
/// intact for downstream tools that legitimately need NyxID tenant scope
/// (binding store, service invocation, etc.).
/// </summary>
public static class ChannelUserConfigScope
{
    private const string DefaultScope = "default";
    private const string DefaultPlatform = "channel";

    public static string FromInboundEvent(ChannelInboundEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return Compose(evt.RegistrationScopeId, evt.Platform, evt.SenderId);
    }

    public static string FromMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
            return DefaultScope;

        metadata.TryGetValue("scope_id", out var scope);
        metadata.TryGetValue(ChannelMetadataKeys.Platform, out var platform);
        metadata.TryGetValue(ChannelMetadataKeys.SenderId, out var senderId);
        return Compose(scope, platform, senderId);
    }

    private static string Compose(string? scopeId, string? platform, string? senderId)
    {
        var normalizedScope = string.IsNullOrWhiteSpace(scopeId) ? DefaultScope : scopeId.Trim();
        var normalizedSender = senderId?.Trim();
        if (string.IsNullOrEmpty(normalizedSender))
            return normalizedScope;

        var normalizedPlatform = string.IsNullOrWhiteSpace(platform)
            ? DefaultPlatform
            : platform.Trim().ToLowerInvariant();
        return $"{normalizedScope}:{normalizedPlatform}:{normalizedSender}";
    }
}
