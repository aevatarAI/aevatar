using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageGAgentActorStore : IGAgentActorStore
{
    private const string ActorsFileName = "actors.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ChronoStorageCatalogBlobClient _blobClient;
    private readonly ConnectorCatalogStorageOptions _options;
    private readonly ILogger<ChronoStorageGAgentActorStore> _logger;

    public ChronoStorageGAgentActorStore(
        ChronoStorageCatalogBlobClient blobClient,
        IOptions<ConnectorCatalogStorageOptions> options,
        ILogger<ChronoStorageGAgentActorStore> logger)
    {
        _blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<GAgentActorGroup>> GetAsync(CancellationToken cancellationToken = default)
    {
        var remoteContext = TryResolve();
        if (remoteContext is null)
            return [];

        var payload = await _blobClient.TryDownloadAsync(remoteContext, cancellationToken);
        if (payload is null)
            return [];

        return DeserializeGroups(payload);
    }

    public async Task AddActorAsync(string gagentType, string actorId, CancellationToken cancellationToken = default)
    {
        var remoteContext = TryResolve()
            ?? throw new InvalidOperationException("GAgent actor storage is not available.");

        var existing = await DownloadGroupsAsync(remoteContext, cancellationToken);
        var group = existing.Find(g => string.Equals(g.GAgentType, gagentType, StringComparison.Ordinal));

        if (group is not null)
        {
            if (group.ActorIds.Contains(actorId))
                return;

            var updatedIds = group.ActorIds.Append(actorId).ToList().AsReadOnly();
            var idx = existing.IndexOf(group);
            existing[idx] = new GAgentActorGroup(gagentType, updatedIds);
        }
        else
        {
            existing.Add(new GAgentActorGroup(gagentType, new[] { actorId }));
        }

        await UploadGroupsAsync(remoteContext, existing, cancellationToken);
    }

    public async Task RemoveActorAsync(string gagentType, string actorId, CancellationToken cancellationToken = default)
    {
        var remoteContext = TryResolve()
            ?? throw new InvalidOperationException("GAgent actor storage is not available.");

        var existing = await DownloadGroupsAsync(remoteContext, cancellationToken);
        var group = existing.Find(g => string.Equals(g.GAgentType, gagentType, StringComparison.Ordinal));

        if (group is null)
            return;

        var updatedIds = group.ActorIds.Where(id => !string.Equals(id, actorId, StringComparison.Ordinal)).ToList().AsReadOnly();
        var idx = existing.IndexOf(group);

        if (updatedIds.Count == 0)
            existing.RemoveAt(idx);
        else
            existing[idx] = new GAgentActorGroup(gagentType, updatedIds);

        await UploadGroupsAsync(remoteContext, existing, cancellationToken);
    }

    private ChronoStorageCatalogBlobClient.RemoteScopeContext? TryResolve()
    {
        try
        {
            return _blobClient.TryResolveContext(_options.UserConfigPrefix, ActorsFileName);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Chrono-storage context could not be resolved for GAgent actor store");
            return null;
        }
    }

    private async Task<List<GAgentActorGroup>> DownloadGroupsAsync(
        ChronoStorageCatalogBlobClient.RemoteScopeContext remoteContext,
        CancellationToken cancellationToken)
    {
        var payload = await _blobClient.TryDownloadAsync(remoteContext, cancellationToken);
        return payload is null ? [] : DeserializeGroups(payload);
    }

    private async Task UploadGroupsAsync(
        ChronoStorageCatalogBlobClient.RemoteScopeContext remoteContext,
        List<GAgentActorGroup> groups,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(groups, JsonOptions);
        await _blobClient.UploadAsync(remoteContext, json, "application/json", cancellationToken);
    }

    private static List<GAgentActorGroup> DeserializeGroups(byte[] payload)
    {
        var doc = JsonDocument.Parse(payload);
        var result = new List<GAgentActorGroup>();

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var gagentType = element.TryGetProperty("gagentType", out var typeProp)
                ? typeProp.GetString() ?? string.Empty
                : string.Empty;

            var actorIds = new List<string>();
            if (element.TryGetProperty("actorIds", out var idsProp) && idsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in idsProp.EnumerateArray())
                {
                    var val = id.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        actorIds.Add(val);
                }
            }

            if (!string.IsNullOrWhiteSpace(gagentType))
                result.Add(new GAgentActorGroup(gagentType, actorIds.AsReadOnly()));
        }

        return result;
    }
}
