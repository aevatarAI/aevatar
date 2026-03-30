using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageUserConfigStore : IUserConfigStore
{
    private const string ConfigFileName = "config.json";

    private static readonly UserConfig DefaultConfig = new(DefaultModel: string.Empty);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ChronoStorageCatalogBlobClient _blobClient;
    private readonly ConnectorCatalogStorageOptions _options;
    private readonly ILogger<ChronoStorageUserConfigStore> _logger;

    public ChronoStorageUserConfigStore(
        ChronoStorageCatalogBlobClient blobClient,
        IOptions<ConnectorCatalogStorageOptions> options,
        ILogger<ChronoStorageUserConfigStore> logger)
    {
        _blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<UserConfig> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var remoteContext = _blobClient.TryResolveContext(_options.UserConfigPrefix, ConfigFileName);
            if (remoteContext is null)
                return DefaultConfig;

            var payload = await _blobClient.TryDownloadAsync(remoteContext, cancellationToken);
            if (payload is null)
                return DefaultConfig;

            var doc = JsonDocument.Parse(payload);
            var defaultModel = doc.RootElement.TryGetProperty("defaultModel", out var modelElement)
                ? modelElement.GetString() ?? string.Empty
                : string.Empty;

            return new UserConfig(DefaultModel: defaultModel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read user config from chrono-storage; returning default config");
            return DefaultConfig;
        }
    }

    public async Task SaveAsync(UserConfig config, CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.UserConfigPrefix, ConfigFileName);
        if (remoteContext is null)
            throw new InvalidOperationException(
                "User config storage is not available. Chrono-storage is disabled or the remote context could not be resolved.");

        var json = JsonSerializer.SerializeToUtf8Bytes(new { defaultModel = config.DefaultModel }, JsonOptions);
        await _blobClient.UploadAsync(remoteContext, json, "application/json", cancellationToken);
    }
}
