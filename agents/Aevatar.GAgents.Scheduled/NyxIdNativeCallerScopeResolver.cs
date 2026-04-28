using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Native NyxID-surface caller-scope resolver (cli + web). Resolves the caller's
/// <see cref="OwnerScope"/> by querying NyxID `/me` with the request's NyxID access
/// token; the resulting scope is <c>(NyxUserId, "nyxid", "", "")</c>.
///
/// Returns <c>null</c> when the request has no NyxID access token at all (the composite
/// resolver tries the next strategy). Throws <see cref="CallerScopeUnavailableException"/>
/// when a token is present but `/me` returns an error envelope or malformed payload —
/// fail-closed rather than fall through to permissive behavior.
/// </summary>
public sealed class NyxIdNativeCallerScopeResolver : ICallerScopeResolver
{
    private readonly INyxIdCurrentUserResolver _nyxIdCurrentUserResolver;

    public NyxIdNativeCallerScopeResolver(INyxIdCurrentUserResolver nyxIdCurrentUserResolver)
    {
        _nyxIdCurrentUserResolver = nyxIdCurrentUserResolver
            ?? throw new ArgumentNullException(nameof(nyxIdCurrentUserResolver));
    }

    public async Task<OwnerScope?> TryResolveAsync(CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            // Not authenticated; the upstream tool layer should already have rejected the
            // request, but if we get here the composite resolver has no fallback to make
            // up for a missing token.
            return null;
        }

        var nyxUserId = await _nyxIdCurrentUserResolver.ResolveCurrentUserIdAsync(token, ct);
        if (string.IsNullOrWhiteSpace(nyxUserId))
        {
            throw new CallerScopeUnavailableException(
                "Could not resolve current NyxID user id (NyxID `/me` returned an error envelope or malformed payload). Refusing to fall through to permissive scope.");
        }

        return OwnerScope.ForNyxIdNative(nyxUserId.Trim());
    }
}
