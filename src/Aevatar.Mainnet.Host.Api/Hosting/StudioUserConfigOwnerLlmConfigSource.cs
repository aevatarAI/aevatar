using Aevatar.AI.Abstractions;
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

        return new OwnerLlmConfig(
            Selection: config.LlmSelection?.Clone() ?? LLMSelectionPolicy.SystemDefaultSelection(),
            Status: LLMSelectionPolicy.ClassifyPersisted(
                config.LlmSelection,
                config.PreferredLlmRoute,
                config.DefaultModel),
            MaxToolRounds: config.MaxToolRounds);
    }
}
