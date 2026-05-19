using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

// Refactor (iter16/cluster-meta-studio-actor-substrate):
//   Old: runtime and appearance settings were read from and written to the local workspace store.
//   New principle: workspace settings flow through workspace query/command ports while provider secrets remain in the settings store boundary.
public sealed class SettingsService
{
    private static readonly HttpClient RuntimeProbeClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    private readonly IStudioWorkspaceQueryPort _workspaceQueryPort;
    private readonly IStudioWorkspaceCommandPort _workspaceCommandPort;
    private readonly IAevatarSettingsStore _aevatarSettingsStore;

    public SettingsService(
        IStudioWorkspaceQueryPort workspaceQueryPort,
        IStudioWorkspaceCommandPort workspaceCommandPort,
        IAevatarSettingsStore aevatarSettingsStore)
    {
        _workspaceQueryPort = workspaceQueryPort;
        _workspaceCommandPort = workspaceCommandPort;
        _aevatarSettingsStore = aevatarSettingsStore;
    }

    public async Task<StudioSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var workspace = (await _workspaceQueryPort.GetAsync(cancellationToken)).Settings;
        var aevatar = await _aevatarSettingsStore.GetAsync(cancellationToken);
        return ToResponse(workspace.RuntimeBaseUrl, workspace.AppearanceTheme, workspace.ColorMode, aevatar);
    }

    public async Task<StudioSettingsResponse> SaveAsync(
        UpdateStudioSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var workspace = await _workspaceQueryPort.GetAsync(cancellationToken);
        var settings = workspace.Settings;
        var runtimeBaseUrl = string.IsNullOrWhiteSpace(request.RuntimeBaseUrl)
            ? settings.RuntimeBaseUrl
            : NormalizeRuntimeBaseUrl(request.RuntimeBaseUrl);
        var appearanceTheme = string.IsNullOrWhiteSpace(request.AppearanceTheme)
            ? settings.AppearanceTheme
            : NormalizeAppearanceTheme(request.AppearanceTheme);
        var colorMode = string.IsNullOrWhiteSpace(request.ColorMode)
            ? settings.ColorMode
            : NormalizeColorMode(request.ColorMode);

        if (!string.Equals(runtimeBaseUrl, settings.RuntimeBaseUrl, StringComparison.Ordinal) ||
            !string.Equals(appearanceTheme, settings.AppearanceTheme, StringComparison.Ordinal) ||
            !string.Equals(colorMode, settings.ColorMode, StringComparison.Ordinal))
        {
            await _workspaceCommandPort.UpdateSettingsAsync(settings with
            {
                RuntimeBaseUrl = runtimeBaseUrl,
                AppearanceTheme = appearanceTheme,
                ColorMode = colorMode,
            }, workspace.StateVersion, cancellationToken);
        }

        var current = await _aevatarSettingsStore.GetAsync(cancellationToken);
        var saved = request.Providers is null
            ? current
            : await _aevatarSettingsStore.SaveAsync(new StoredAevatarSettings(
                current.SecretsFilePath,
                request.DefaultProviderName?.Trim() ?? current.DefaultProviderName,
                current.ProviderTypes,
                request.Providers
                    .Select(provider => new StoredLlmProvider(
                        provider.ProviderName,
                        provider.ProviderType,
                        DisplayName: string.Empty,
                        Category: string.Empty,
                        Description: string.Empty,
                        provider.Model,
                        provider.Endpoint ?? string.Empty,
                        provider.ApiKey ?? string.Empty,
                        ApiKeyConfigured: !string.IsNullOrWhiteSpace(provider.ApiKey)))
                    .ToList()), cancellationToken);

        return ToResponse(runtimeBaseUrl, appearanceTheme, colorMode, saved);
    }

    public async Task<RuntimeConnectionTestResponse> TestRuntimeAsync(
        RuntimeConnectionTestRequest request,
        CancellationToken cancellationToken = default)
    {
        var workspace = (await _workspaceQueryPort.GetAsync(cancellationToken)).Settings;
        var runtimeBaseUrl = NormalizeRuntimeBaseUrl(string.IsNullOrWhiteSpace(request.RuntimeBaseUrl)
            ? workspace.RuntimeBaseUrl
            : request.RuntimeBaseUrl!);

        if (!Uri.TryCreate(runtimeBaseUrl, UriKind.Absolute, out var runtimeUri) ||
            (runtimeUri.Scheme != Uri.UriSchemeHttp && runtimeUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Runtime URL must be a valid http or https address.");
        }

        var candidates = new[]
        {
            runtimeBaseUrl,
            new Uri(runtimeUri, "api/openapi.json").ToString(),
        };

        Exception? lastError = null;
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, candidate);
                using var response = await RuntimeProbeClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var statusCode = (int)response.StatusCode;
                var reachableMessage = response.IsSuccessStatusCode
                    ? $"Connected successfully ({statusCode})."
                    : $"Runtime is reachable and responded with HTTP {statusCode}.";

                return new RuntimeConnectionTestResponse(
                    RuntimeBaseUrl: runtimeBaseUrl,
                    Reachable: true,
                    CheckedUrl: candidate,
                    StatusCode: statusCode,
                    Message: reachableMessage);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastError = exception;
            }
        }

        return new RuntimeConnectionTestResponse(
            RuntimeBaseUrl: runtimeBaseUrl,
            Reachable: false,
            CheckedUrl: candidates[^1],
            StatusCode: null,
            Message: lastError?.Message ?? "Failed to reach the runtime.");
    }

    private static StudioSettingsResponse ToResponse(string runtimeBaseUrl, string appearanceTheme, string colorMode, StoredAevatarSettings aevatar) =>
        new(
            runtimeBaseUrl,
            appearanceTheme,
            colorMode,
            aevatar.SecretsFilePath,
            aevatar.DefaultProviderName,
            aevatar.ProviderTypes
                .Select(item => new LlmProviderTypeSummary(
                    item.Id,
                    item.DisplayName,
                    item.Category,
                    item.Description,
                    item.Recommended,
                    item.DefaultEndpoint,
                    item.DefaultModel))
                .ToList(),
            aevatar.Providers
                .Select(item => new LlmProviderSummary(
                    item.ProviderName,
                    item.ProviderType,
                    item.DisplayName,
                    item.Category,
                    item.Description,
                    item.Model,
                    item.Endpoint,
                    item.ApiKey,
                    item.ApiKeyConfigured))
                .ToList());

    private static string NormalizeRuntimeBaseUrl(string url)
    {
        var normalized = string.IsNullOrWhiteSpace(url)
            ? UserConfigRuntimeDefaults.LocalRuntimeBaseUrl
            : url.Trim();

        return normalized.TrimEnd('/');
    }

    private static string NormalizeAppearanceTheme(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "coral" => "coral",
            "forest" => "forest",
            _ => "blue",
        };
    }

    private static string NormalizeColorMode(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "dark" => "dark",
            _ => "light",
        };
    }
}
