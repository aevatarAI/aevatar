using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageNyxIdUserLlmPreferencesStore : INyxIdUserLlmPreferencesStore
{
    private readonly IUserConfigStore _userConfigStore;

    public ChronoStorageNyxIdUserLlmPreferencesStore(IUserConfigStore userConfigStore)
    {
        _userConfigStore = userConfigStore ?? throw new ArgumentNullException(nameof(userConfigStore));
    }

    public async Task<NyxIdUserLlmPreferences> GetAsync(CancellationToken cancellationToken = default)
    {
        var config = await _userConfigStore.GetAsync(cancellationToken);
        return new NyxIdUserLlmPreferences(
            config.DefaultModel,
            UserConfigLlmRoute.Normalize(config.PreferredLlmRoute),
            config.MaxToolRounds);
    }
}
