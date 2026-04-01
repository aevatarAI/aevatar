namespace Aevatar.AI.Abstractions.LLMProviders;

public interface INyxIdUserLlmPreferencesStore
{
    Task<NyxIdUserLlmPreferences> GetAsync(CancellationToken cancellationToken = default);
}

public sealed record NyxIdUserLlmPreferences(
    string DefaultModel,
    string PreferredRoute);
