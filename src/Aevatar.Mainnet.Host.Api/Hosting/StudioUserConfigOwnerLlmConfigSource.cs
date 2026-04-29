using Aevatar.GAgents.Scheduled;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Mainnet.Host.Api.Hosting;

/// <summary>
/// Bridges the Studio Application's <see cref="IUserConfigQueryPort"/> to the Scheduled agents'
/// narrow <see cref="IOwnerLlmConfigSource"/>. Lives in the host because the host is the only
/// layer that legitimately depends on both projects — keeping the bridge here lets the Scheduled
/// package stay free of any Studio.Application reference (per architecture review on PR #509).
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

        return new OwnerLlmConfig(
            DefaultModel: config.DefaultModel,
            PreferredLlmRoute: config.PreferredLlmRoute,
            MaxToolRounds: config.MaxToolRounds);
    }
}
