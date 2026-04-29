using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Read-only view that extracts LLM preferences from the user-config
/// projection via <see cref="IUserConfigQueryPort"/>. When called with a
/// sender binding-id, scopes the lookup to <c>user-config-&lt;binding-id&gt;</c>
/// so /init-bound senders carry their own model / route across chats; when
/// called with <c>null</c>, falls back to the ambient (bot-owner) scope so
/// existing studio + streaming-proxy callers behave as before.
/// </summary>
internal sealed class ActorBackedNyxIdUserLlmPreferencesStore : INyxIdUserLlmPreferencesStore
{
    private readonly IUserConfigQueryPort _queryPort;

    public ActorBackedNyxIdUserLlmPreferencesStore(IUserConfigQueryPort queryPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    }

    public async Task<NyxIdUserLlmPreferences> GetAsync(string? senderBindingId, CancellationToken cancellationToken = default)
    {
        var config = string.IsNullOrWhiteSpace(senderBindingId)
            ? await _queryPort.GetAsync(cancellationToken)
            : await _queryPort.GetAsync(senderBindingId.Trim(), cancellationToken);

        return new NyxIdUserLlmPreferences(
            config.DefaultModel,
            UserConfigLlmRoute.Normalize(config.PreferredLlmRoute),
            config.MaxToolRounds);
    }
}
