using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageUserConfigStore : IUserConfigStore
{
    private const string ConfigFileName = "config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ChronoStorageCatalogBlobClient _blobClient;
    private readonly ConnectorCatalogStorageOptions _options;
    private readonly string _defaultLocalRuntimeBaseUrl;
    private readonly string _defaultRemoteRuntimeBaseUrl;
    private readonly ILogger<ChronoStorageUserConfigStore> _logger;

    public ChronoStorageUserConfigStore(
        ChronoStorageCatalogBlobClient blobClient,
        IOptions<ConnectorCatalogStorageOptions> options,
        IOptions<StudioStorageOptions> studioStorageOptions,
        ILogger<ChronoStorageUserConfigStore> logger)
    {
        _blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        var resolvedStudioStorageOptions = studioStorageOptions?.Value.ResolveRootDirectory()
            ?? throw new ArgumentNullException(nameof(studioStorageOptions));
        _defaultLocalRuntimeBaseUrl = resolvedStudioStorageOptions.ResolveDefaultLocalRuntimeBaseUrl();
        _defaultRemoteRuntimeBaseUrl = resolvedStudioStorageOptions.ResolveDefaultRemoteRuntimeBaseUrl();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<UserConfig> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var remoteContext = _blobClient.TryResolveContext(_options.UserConfigPrefix, ConfigFileName);
            if (remoteContext is null)
                return CreateDefaultConfig();

            var payload = await _blobClient.TryDownloadAsync(remoteContext, cancellationToken);
            if (payload is null)
                return CreateDefaultConfig();

            var doc = JsonDocument.Parse(payload);
            var defaultModel = doc.RootElement.TryGetProperty("defaultModel", out var modelElement)
                ? modelElement.GetString() ?? string.Empty
                : string.Empty;
            var preferredLlmRoute = doc.RootElement.TryGetProperty("preferredLlmRoute", out var routeElement)
                ? UserConfigLlmRoute.Normalize(routeElement.GetString())
                : UserConfigLlmRouteDefaults.Gateway;

            var hasRuntimeMode = doc.RootElement.TryGetProperty("runtimeMode", out var runtimeModeElement);
            var hasLocalRuntimeBaseUrl = doc.RootElement.TryGetProperty("localRuntimeBaseUrl", out var localRuntimeElement);
            var hasRemoteRuntimeBaseUrl = doc.RootElement.TryGetProperty("remoteRuntimeBaseUrl", out var remoteRuntimeElement);

            if (hasRuntimeMode || hasLocalRuntimeBaseUrl || hasRemoteRuntimeBaseUrl)
            {
                return new UserConfig(
                    DefaultModel: defaultModel,
                    PreferredLlmRoute: preferredLlmRoute,
                    RuntimeMode: UserConfigRuntime.NormalizeMode(hasRuntimeMode ? runtimeModeElement.GetString() : null),
                    LocalRuntimeBaseUrl: UserConfigRuntime.NormalizeBaseUrl(
                        hasLocalRuntimeBaseUrl ? localRuntimeElement.GetString() : null,
                        _defaultLocalRuntimeBaseUrl),
                    RemoteRuntimeBaseUrl: UserConfigRuntime.NormalizeBaseUrl(
                        hasRemoteRuntimeBaseUrl ? remoteRuntimeElement.GetString() : null,
                        _defaultRemoteRuntimeBaseUrl));
            }

            var legacyRuntimeBaseUrl = doc.RootElement.TryGetProperty("runtimeBaseUrl", out var runtimeElement)
                ? runtimeElement.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(legacyRuntimeBaseUrl))
            {
                return CreateDefaultConfig() with
                {
                    DefaultModel = defaultModel,
                    PreferredLlmRoute = preferredLlmRoute,
                };
            }

            var normalizedLegacyRuntimeBaseUrl = legacyRuntimeBaseUrl.Trim().TrimEnd('/');
            var runtimeMode = UserConfigRuntime.IsLoopbackRuntime(normalizedLegacyRuntimeBaseUrl)
                ? UserConfigRuntimeDefaults.LocalMode
                : UserConfigRuntimeDefaults.RemoteMode;

            return runtimeMode == UserConfigRuntimeDefaults.RemoteMode
                ? new UserConfig(
                    DefaultModel: defaultModel,
                    PreferredLlmRoute: preferredLlmRoute,
                    RuntimeMode: runtimeMode,
                    LocalRuntimeBaseUrl: _defaultLocalRuntimeBaseUrl,
                    RemoteRuntimeBaseUrl: normalizedLegacyRuntimeBaseUrl)
                : new UserConfig(
                    DefaultModel: defaultModel,
                    PreferredLlmRoute: preferredLlmRoute,
                    RuntimeMode: runtimeMode,
                    LocalRuntimeBaseUrl: normalizedLegacyRuntimeBaseUrl,
                    RemoteRuntimeBaseUrl: _defaultRemoteRuntimeBaseUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read user config from chrono-storage; returning default config");
            return CreateDefaultConfig();
        }
    }

    public async Task SaveAsync(UserConfig config, CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.UserConfigPrefix, ConfigFileName);
        if (remoteContext is null)
            throw new InvalidOperationException(
                "User config storage is not available. Chrono-storage is disabled or the remote context could not be resolved.");

        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            defaultModel = config.DefaultModel,
            preferredLlmRoute = UserConfigLlmRoute.Normalize(config.PreferredLlmRoute),
            runtimeMode = UserConfigRuntime.NormalizeMode(config.RuntimeMode),
            localRuntimeBaseUrl = UserConfigRuntime.NormalizeBaseUrl(
                config.LocalRuntimeBaseUrl,
                _defaultLocalRuntimeBaseUrl),
            remoteRuntimeBaseUrl = UserConfigRuntime.NormalizeBaseUrl(
                config.RemoteRuntimeBaseUrl,
                _defaultRemoteRuntimeBaseUrl),
        }, JsonOptions);
        await _blobClient.UploadAsync(remoteContext, json, "application/json", cancellationToken);
    }

    private UserConfig CreateDefaultConfig() =>
        new(
            DefaultModel: string.Empty,
            PreferredLlmRoute: UserConfigLlmRouteDefaults.Gateway,
            RuntimeMode: UserConfigRuntimeDefaults.LocalMode,
            LocalRuntimeBaseUrl: _defaultLocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl: _defaultRemoteRuntimeBaseUrl);
}
