namespace Aevatar.Studio.Application.Studio.Contracts;

public sealed record StudioSettingsResponse(
    string RuntimeBaseUrl,
    string AppearanceTheme,
    string ColorMode,
    string SecretsFilePath,
    string DefaultProviderName,
    IReadOnlyList<LlmProviderTypeSummary> ProviderTypes,
    IReadOnlyList<LlmProviderSummary> Providers);

public sealed record LlmProviderTypeSummary(
    string Id,
    string DisplayName,
    string Category,
    string Description,
    bool Recommended,
    string DefaultEndpoint,
    string DefaultModel);

public sealed record LlmProviderSummary(
    string ProviderName,
    string ProviderType,
    string DisplayName,
    string Category,
    string Description,
    string Model,
    string Endpoint,
    string ApiKey,
    bool ApiKeyConfigured);

public sealed record UpdateStudioSettingsRequest(
    string? RuntimeBaseUrl = null,
    string? AppearanceTheme = null,
    string? ColorMode = null,
    string? DefaultProviderName = null,
    IReadOnlyList<UpdateLlmProviderRequest>? Providers = null);

public sealed record RuntimeConnectionTestRequest(
    string? RuntimeBaseUrl = null);

public sealed record RuntimeConnectionTestResponse(
    string RuntimeBaseUrl,
    bool Reachable,
    string CheckedUrl,
    int? StatusCode,
    string Message);

public sealed record UpdateLlmProviderRequest(
    string ProviderName,
    string ProviderType,
    string Model,
    string? Endpoint = null,
    string? ApiKey = null);
