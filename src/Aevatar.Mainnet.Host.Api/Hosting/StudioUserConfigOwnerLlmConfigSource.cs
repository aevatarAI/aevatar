using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Mainnet.Host.Api.Hosting;

/// <summary>
/// Bridges the Studio Application's <see cref="IUserConfigQueryPort"/> to the AI-layer
/// <see cref="IOwnerLlmConfigSource"/>. Lives in the host because the host is the only layer
/// that legitimately depends on both projects — keeping the bridge here lets the consuming
/// agent / AI packages stay free of any Studio.Application reference (per architecture review
/// on PR #509).
/// </summary>
internal sealed class StudioUserConfigOwnerLlmConfigSource : IOwnerLlmConfigSource
{
    private readonly IUserConfigQueryPort _queryPort;

    public StudioUserConfigOwnerLlmConfigSource(IUserConfigQueryPort queryPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    }

    public async Task<OwnerLlmConfig> GetForScopeAsync(string scopeId, CancellationToken ct = default)
    {
        var config = await _queryPort.GetAsync(UserConfigResourceKey.ForOwnerScope(scopeId), ct);
        if (config is null)
            return OwnerLlmConfig.Empty;

        // OwnerLlmConfig uses null to leave the provider's default gateway route unpinned.
        // Normalize first so all gateway aliases and invalid external routes collapse to the
        // canonical gateway value, then translate that value to the AI-layer null sentinel.
        var normalizedRoute = UserConfigLlmRoute.Normalize(config.PreferredLlmRoute);
        var preferredRoute = string.Equals(
            normalizedRoute,
            UserConfigLlmRouteDefaults.Gateway,
            StringComparison.OrdinalIgnoreCase)
            ? null
            : NormalizeOptional(normalizedRoute);

        return new OwnerLlmConfig(
            DefaultModel: NormalizeOptional(config.DefaultModel),
            PreferredLlmRoute: preferredRoute,
            MaxToolRounds: config.MaxToolRounds);
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
