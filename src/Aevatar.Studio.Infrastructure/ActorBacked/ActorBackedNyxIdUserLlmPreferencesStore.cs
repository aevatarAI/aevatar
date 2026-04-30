using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Read-only view that extracts LLM preferences from the user-config
/// projection via <see cref="IUserConfigQueryPort"/>. Two distinct entry
/// points scope the read explicitly: <see cref="GetOwnerAsync"/> for the
/// bot-owner ambient scope (Studio API, streaming proxy), and
/// <see cref="GetForBindingAsync"/> for the sender's
/// <c>user-config-&lt;binding-id&gt;</c> actor (channel inbound).
/// </summary>
internal sealed class ActorBackedNyxIdUserLlmPreferencesStore : INyxIdUserLlmPreferencesStore
{
    private readonly IUserConfigQueryPort _queryPort;

    public ActorBackedNyxIdUserLlmPreferencesStore(IUserConfigQueryPort queryPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    }

    public async Task<NyxIdUserLlmPreferences> GetOwnerAsync(CancellationToken cancellationToken = default)
    {
        var config = await _queryPort.GetAsync(cancellationToken);
        return Project(config);
    }

    public async Task<NyxIdUserLlmPreferences> GetForBindingAsync(string bindingId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingId);
        var config = await _queryPort.GetAsync(bindingId.Trim(), cancellationToken);
        return Project(config);
    }

    private static NyxIdUserLlmPreferences Project(UserConfig config) => new(
        config.DefaultModel,
        UserConfigLlmRoute.Normalize(config.PreferredLlmRoute),
        config.MaxToolRounds);
}
