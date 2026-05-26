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
        var config = await _queryPort.GetAsync(scopeId, ct);
        if (config is null)
            return OwnerLlmConfig.Empty;

        // ProjectionUserConfigQueryPort fills `PreferredLlmRoute` with `UserConfigLlmRouteDefaults.Gateway`
        // when the user has no saved route or explicitly chose the gateway. Today that sentinel
        // is empty-string, so the applier's null-or-whitespace guard already filters it out, but
        // routing through `UserConfigLlmRoute.Normalize` here makes the contract explicit and
        // future-proof: any "use the default gateway" sentinel — `""` / `"auto"` / `"gateway"`
        // / invalid URI — collapses to `null`, the applier leaves `NyxIdRoutePreference` unset,
        // and the LLM provider's compile-time gateway path takes over without any sentinel
        // value leaking into outbound metadata. (Codex flagged this as a future-bug risk on
        // PR #509 — the normalization is the explicit fix.)
        var normalizedRoute = UserConfigLlmRoute.Normalize(config.PreferredLlmRoute);

        return new OwnerLlmConfig(
            DefaultModel: NormalizeOptional(config.DefaultModel),
            PreferredLlmRoute: NormalizeOptional(normalizedRoute),
            MaxToolRounds: config.MaxToolRounds);
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
