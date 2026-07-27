using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
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
/// The <c>nyx_user_id</c> field is populated from typed sender binding context when
/// available, then from <c>Caller.OwnerScopeId</c>. NyxID <c>/me</c> is only a legacy
/// enrichment fallback for channel requests that predate those typed fields.
///
/// Returns <c>null</c> when the request context has no channel platform metadata (the
/// composite resolver tries the next strategy). Throws
/// <see cref="CallerScopeUnavailableException"/> when channel metadata is present but
/// incomplete (missing sender_id / owner subject failure etc.) — that fails closed rather
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
        var platform = NormalizeOptional(AgentToolRequestContext.ChannelPlatform);
        if (platform is null)
        {
            // Not a channel-surface request; let the composite try the next resolver.
            return null;
        }

        var senderId = NormalizeOptional(AgentToolRequestContext.ChannelSenderId);
        if (senderId is null)
        {
            throw new CallerScopeUnavailableException(
                $"Channel platform metadata is present (platform=\"{platform}\") but channel.sender_id is missing. Cannot scope agent operations safely.");
        }

        // Bot's registration scope. Empty/missing is a misconfiguration on a channel surface;
        // every channel bot has a registration scope by construction.
        var registrationScopeId = NormalizeOptional(AgentToolRequestContext.ChannelRegistrationScopeId ?? AgentToolRequestContext.ScopeId);
        if (registrationScopeId is null)
        {
            throw new CallerScopeUnavailableException(
                $"Channel platform metadata is present (platform=\"{platform}\") but scope_id is missing. Cannot scope agent operations safely.");
        }

        var nyxUserId = NormalizeOptional(AgentToolRequestContext.SenderNyxUserId) ??
                        NormalizeOptional(AgentToolRequestContext.OwnerScopeId);
        if (string.IsNullOrWhiteSpace(nyxUserId))
        {
            var token = AgentToolRequestContext.NyxIdAccessToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new CallerScopeUnavailableException(
                    "No typed sender owner context or NyxID access token available; cannot resolve caller ownership scope.");
            }

            nyxUserId = await _nyxIdCurrentUserResolver.ResolveCurrentUserIdAsync(token, ct);
            if (string.IsNullOrWhiteSpace(nyxUserId))
            {
                throw new CallerScopeUnavailableException(
                    "Could not resolve current NyxID user id (NyxID `/me` returned an error envelope or malformed payload) and no typed sender owner context was available. Refusing to fall through to permissive scope.");
            }
        }

        return OwnerScope.ForChannel(nyxUserId.Trim(), platform, registrationScopeId, senderId);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
