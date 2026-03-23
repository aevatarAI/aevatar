using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Aevatar.Tools.Cli.Studio.Application.Contracts;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Studio.Application.Services;

public sealed class SettingsService
{
    private static readonly HttpClient RuntimeProbeClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    private readonly IStudioWorkspaceStore _workspaceStore;
    private readonly IAevatarSettingsStore _aevatarSettingsStore;
    private readonly AppRuntimeTargetResolver? _runtimeTargetResolver;

    public SettingsService(
        IStudioWorkspaceStore workspaceStore,
        IAevatarSettingsStore aevatarSettingsStore,
        AppRuntimeTargetResolver? runtimeTargetResolver = null)
    {
        _workspaceStore = workspaceStore;
        _aevatarSettingsStore = aevatarSettingsStore;
        _runtimeTargetResolver = runtimeTargetResolver;
    }

    public async Task<StudioSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var workspace = await _workspaceStore.GetSettingsAsync(cancellationToken);
        var aevatar = await _aevatarSettingsStore.GetAsync(cancellationToken);
        return ToResponse(
            await ResolveRuntimeBaseUrlAsync(workspace.RuntimeBaseUrl, cancellationToken),
            workspace.AppearanceTheme,
            workspace.ColorMode,
            aevatar);
    }

    public async Task<StudioSettingsResponse> SaveAsync(
        UpdateStudioSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var workspace = await _workspaceStore.GetSettingsAsync(cancellationToken);
        var runtimeBaseUrl = string.IsNullOrWhiteSpace(request.RuntimeBaseUrl)
            ? await ResolveRuntimeBaseUrlAsync(workspace.RuntimeBaseUrl, cancellationToken)
            : NormalizeRuntimeBaseUrl(request.RuntimeBaseUrl);
        var appearanceTheme = string.IsNullOrWhiteSpace(request.AppearanceTheme)
            ? workspace.AppearanceTheme
            : NormalizeAppearanceTheme(request.AppearanceTheme);
        var colorMode = string.IsNullOrWhiteSpace(request.ColorMode)
            ? workspace.ColorMode
            : NormalizeColorMode(request.ColorMode);

        if (!string.Equals(runtimeBaseUrl, workspace.RuntimeBaseUrl, StringComparison.Ordinal) ||
            !string.Equals(appearanceTheme, workspace.AppearanceTheme, StringComparison.Ordinal) ||
            !string.Equals(colorMode, workspace.ColorMode, StringComparison.Ordinal))
        {
            await _workspaceStore.SaveSettingsAsync(workspace with
            {
                RuntimeBaseUrl = runtimeBaseUrl,
                AppearanceTheme = appearanceTheme,
                ColorMode = colorMode,
            }, cancellationToken);
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
        var workspace = await _workspaceStore.GetSettingsAsync(cancellationToken);
        var runtimeBaseUrl = NormalizeRuntimeBaseUrl(string.IsNullOrWhiteSpace(request.RuntimeBaseUrl)
            ? await ResolveRuntimeBaseUrlAsync(workspace.RuntimeBaseUrl, cancellationToken)
            : request.RuntimeBaseUrl!);

        if (!Uri.TryCreate(runtimeBaseUrl, UriKind.Absolute, out var runtimeUri) ||
            (runtimeUri.Scheme != Uri.UriSchemeHttp && runtimeUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Runtime URL must be a valid http or https address.");
        }

        var candidates = new[]
        {
            runtimeBaseUrl,
            new Uri(runtimeUri, "openapi/v1.json").ToString(),
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

    private async Task<string> ResolveRuntimeBaseUrlAsync(string currentRuntimeBaseUrl, CancellationToken cancellationToken)
    {
        if (_runtimeTargetResolver == null)
            return currentRuntimeBaseUrl;

        var runtimeTarget = await _runtimeTargetResolver.GetCurrentAsync(cancellationToken);
        return runtimeTarget.ConfiguredBaseUrl;
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
            ? "http://localhost:6688"
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
