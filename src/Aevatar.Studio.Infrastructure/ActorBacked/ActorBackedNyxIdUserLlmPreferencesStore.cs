using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="INyxIdUserLlmPreferencesStore"/>.
/// Read-only view that extracts LLM preferences from the same
/// <see cref="IUserConfigStore"/> backed by <c>UserConfigGAgent</c>.
/// </summary>
internal sealed class ActorBackedNyxIdUserLlmPreferencesStore : INyxIdUserLlmPreferencesStore
{
    private readonly IUserConfigStore _userConfigStore;

    public ActorBackedNyxIdUserLlmPreferencesStore(IUserConfigStore userConfigStore)
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
