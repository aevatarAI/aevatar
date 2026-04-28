using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Channel-surface caller-scope resolver. Reads the caller identity tuple from the
/// inbound channel metadata that the relay middleware populated on
/// <c>AgentToolRequestContext</c>:
///
/// <list type="bullet">
///   <item><c>channel.platform</c> → <see cref="OwnerScope.Platform"/> (canonical "lark"/"telegram"/…)</item>
///   <item><c>scope_id</c> → <see cref="OwnerScope.RegistrationScopeId"/> (the bot's registration scope)</item>
///   <item><c>channel.sender_id</c> → <see cref="OwnerScope.SenderId"/> (per-sender, not per-conversation; aligns with #436)</item>
/// </list>
///
/// The <c>nyx_user_id</c> is delegated to the inner <see cref="INyxIdCurrentUserResolver"/>
/// (which queries NyxID `/me`) so a channel-bound caller can be linked back to the NyxID
/// account that registered the bot.
///
/// Returns <c>null</c> when the request context has no channel platform metadata (the
/// composite resolver tries the next strategy). Throws
/// <see cref="CallerScopeUnavailableException"/> when channel metadata is present but
/// incomplete (missing sender_id / NyxID `/me` failure etc.) — that fails closed rather
/// than falling through to "all agents".
/// </summary>
public sealed class ChannelMetadataCallerScopeResolver : ICallerScopeResolver
{
    private readonly INyxIdCurrentUserResolver _nyxIdCurrentUserResolver;

    public ChannelMetadataCallerScopeResolver(INyxIdCurrentUserResolver nyxIdCurrentUserResolver)
    {
        _nyxIdCurrentUserResolver = nyxIdCurrentUserResolver
            ?? throw new ArgumentNullException(nameof(nyxIdCurrentUserResolver));
    }

    public async Task<OwnerScope?> TryResolveAsync(CancellationToken ct = default)
    {
        var platform = NormalizeOptional(AgentToolRequestContext.TryGet(ChannelMetadataKeys.Platform));
        if (platform is null)
        {
            // Not a channel-surface request; let the composite try the next resolver.
            return null;
        }

        var senderId = NormalizeOptional(AgentToolRequestContext.TryGet(ChannelMetadataKeys.SenderId));
        if (senderId is null)
        {
            throw new CallerScopeUnavailableException(
                $"Channel platform metadata is present (platform=\"{platform}\") but channel.sender_id is missing. Cannot scope agent operations safely.");
        }

        // Bot's registration scope. Empty/missing is a misconfiguration on a channel surface;
        // every channel bot has a registration scope by construction.
        var registrationScopeId = NormalizeOptional(AgentToolRequestContext.TryGet(ChannelMetadataKeys.RegistrationScopeId));
        if (registrationScopeId is null)
        {
            throw new CallerScopeUnavailableException(
                $"Channel platform metadata is present (platform=\"{platform}\") but scope_id is missing. Cannot scope agent operations safely.");
        }

        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new CallerScopeUnavailableException(
                "No NyxID access token available; cannot resolve caller's NyxID user identity for ownership scope.");
        }

        var nyxUserId = await _nyxIdCurrentUserResolver.ResolveCurrentUserIdAsync(token, ct);
        if (string.IsNullOrWhiteSpace(nyxUserId))
        {
            throw new CallerScopeUnavailableException(
                "Could not resolve current NyxID user id (NyxID `/me` returned an error envelope or malformed payload). Refusing to fall through to permissive scope.");
        }

        return OwnerScope.ForChannel(nyxUserId.Trim(), platform, registrationScopeId, senderId);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
