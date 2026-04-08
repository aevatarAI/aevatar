using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageStreamingProxyParticipantStore : IStreamingProxyParticipantStore
{
    private const string ParticipantsFileName = "streaming-proxy-participants.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ChronoStorageCatalogBlobClient _blobClient;
    private readonly ConnectorCatalogStorageOptions _options;
    private readonly ILogger<ChronoStorageStreamingProxyParticipantStore> _logger;

    public ChronoStorageStreamingProxyParticipantStore(
        ChronoStorageCatalogBlobClient blobClient,
        IOptions<ConnectorCatalogStorageOptions> options,
        ILogger<ChronoStorageStreamingProxyParticipantStore> logger)
    {
        _blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<StreamingProxyParticipant>> ListAsync(
        string roomId, CancellationToken cancellationToken = default)
    {
        var rooms = await DownloadAsync(cancellationToken);
        return rooms.TryGetValue(roomId, out var participants)
            ? participants.AsReadOnly()
            : [];
    }

    public async Task AddAsync(
        string roomId, string agentId, string displayName,
        CancellationToken cancellationToken = default)
    {
        var remoteContext = TryResolve()
            ?? throw new InvalidOperationException("Streaming proxy participant storage is not available.");

        var rooms = await DownloadAsync(cancellationToken);

        if (!rooms.TryGetValue(roomId, out var participants))
        {
            participants = [];
            rooms[roomId] = participants;
        }

        participants.RemoveAll(p => string.Equals(p.AgentId, agentId, StringComparison.Ordinal));
        participants.Add(new StreamingProxyParticipant(agentId, displayName, DateTimeOffset.UtcNow));

        await UploadAsync(remoteContext, rooms, cancellationToken);
    }

    public async Task RemoveParticipantAsync(
        string roomId, string agentId, CancellationToken cancellationToken = default)
    {
        var remoteContext = TryResolve();
        if (remoteContext is null)
            return;

        var rooms = await DownloadAsync(cancellationToken);
        if (!rooms.TryGetValue(roomId, out var participants))
            return;

        var removedCount = participants.RemoveAll(p => string.Equals(p.AgentId, agentId, StringComparison.Ordinal));
        if (removedCount == 0)
            return;

        if (participants.Count == 0)
            rooms.Remove(roomId);

        await UploadAsync(remoteContext, rooms, cancellationToken);
    }

    public async Task RemoveRoomAsync(string roomId, CancellationToken cancellationToken = default)
    {
        var remoteContext = TryResolve();
        if (remoteContext is null)
            return;

        var rooms = await DownloadAsync(cancellationToken);
        if (!rooms.Remove(roomId))
            return;

        await UploadAsync(remoteContext, rooms, cancellationToken);
    }

    private ChronoStorageCatalogBlobClient.RemoteScopeContext? TryResolve()
    {
        try
        {
            return _blobClient.TryResolveContext(_options.UserConfigPrefix, ParticipantsFileName);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Chrono-storage context could not be resolved for streaming proxy participant store");
            return null;
        }
    }

    private async Task<Dictionary<string, List<StreamingProxyParticipant>>> DownloadAsync(
        CancellationToken cancellationToken)
    {
        var remoteContext = TryResolve();
        if (remoteContext is null)
            return new Dictionary<string, List<StreamingProxyParticipant>>(StringComparer.Ordinal);

        var payload = await _blobClient.TryDownloadAsync(remoteContext, cancellationToken);
        if (payload is null)
            return new Dictionary<string, List<StreamingProxyParticipant>>(StringComparer.Ordinal);

        return DeserializeRooms(payload);
    }

    private async Task UploadAsync(
        ChronoStorageCatalogBlobClient.RemoteScopeContext remoteContext,
        Dictionary<string, List<StreamingProxyParticipant>> rooms,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(rooms, JsonOptions);
        await _blobClient.UploadAsync(remoteContext, json, "application/json", cancellationToken);
    }

    private Dictionary<string, List<StreamingProxyParticipant>> DeserializeRooms(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, List<StreamingProxyParticipant>>>(payload, JsonOptions)
                   ?? new Dictionary<string, List<StreamingProxyParticipant>>(StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            // Do NOT return empty — that would cause the next AddAsync to overwrite
            // all existing rooms' participants with a blank snapshot.
            _logger.LogError(ex, "Corrupt participant store payload; refusing to deserialize to prevent data loss");
            throw new InvalidOperationException("Participant store payload is corrupt", ex);
        }
    }
}
