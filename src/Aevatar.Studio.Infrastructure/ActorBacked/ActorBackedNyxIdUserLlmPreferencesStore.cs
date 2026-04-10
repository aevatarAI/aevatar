using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Read-only view that extracts LLM preferences from the user config
/// projection via <see cref="IUserConfigQueryPort"/>.
/// </summary>
internal sealed class ActorBackedNyxIdUserLlmPreferencesStore : INyxIdUserLlmPreferencesStore
{
    private readonly IUserConfigQueryPort _queryPort;

    public ActorBackedNyxIdUserLlmPreferencesStore(IUserConfigQueryPort queryPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    }

    public async Task<NyxIdUserLlmPreferences> GetAsync(CancellationToken cancellationToken = default)
    {
        var config = await _queryPort.GetAsync(cancellationToken);
        return new NyxIdUserLlmPreferences(
            config.DefaultModel,
            UserConfigLlmRoute.Normalize(config.PreferredLlmRoute),
            config.MaxToolRounds);
    }
}
