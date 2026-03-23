namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IAevatarSettingsStore
{
    Task<StoredAevatarSettings> GetAsync(CancellationToken cancellationToken = default);

    Task<StoredAevatarSettings> SaveAsync(StoredAevatarSettings settings, CancellationToken cancellationToken = default);
}

public sealed record StoredAevatarSettings(
    string SecretsFilePath,
    string DefaultProviderName,
    IReadOnlyList<StoredLlmProviderType> ProviderTypes,
    IReadOnlyList<StoredLlmProvider> Providers);

public sealed record StoredLlmProviderType(
    string Id,
    string DisplayName,
    string Category,
    string Description,
    bool Recommended,
    string DefaultEndpoint,
    string DefaultModel);

public sealed record StoredLlmProvider(
    string ProviderName,
    string ProviderType,
    string DisplayName,
    string Category,
    string Description,
    string Model,
    string Endpoint,
    string ApiKey,
    bool ApiKeyConfigured);
