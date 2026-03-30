using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;
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

    public ChronoStorageUserConfigStore(
        ChronoStorageCatalogBlobClient blobClient,
        IOptions<ConnectorCatalogStorageOptions> options)
    {
        _blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<UserConfig> GetAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.UserConfigPrefix, ConfigFileName);
        if (remoteContext is null)
            return new UserConfig(DefaultModel: string.Empty);

        var payload = await _blobClient.TryDownloadAsync(remoteContext, cancellationToken);
        if (payload is null)
            return new UserConfig(DefaultModel: string.Empty);

        var doc = JsonDocument.Parse(payload);
        var defaultModel = doc.RootElement.TryGetProperty("defaultModel", out var modelElement)
            ? modelElement.GetString() ?? string.Empty
            : string.Empty;

        return new UserConfig(DefaultModel: defaultModel);
    }

    public async Task SaveAsync(UserConfig config, CancellationToken cancellationToken = default)
    {
        var remoteContext = _blobClient.TryResolveContext(_options.UserConfigPrefix, ConfigFileName);
        if (remoteContext is null)
            throw new InvalidOperationException("User config storage is not available. Chrono-storage remote context could not be resolved.");

        var json = JsonSerializer.SerializeToUtf8Bytes(new { defaultModel = config.DefaultModel }, JsonOptions);
        await _blobClient.UploadAsync(remoteContext, json, "application/json", cancellationToken);
    }
}
