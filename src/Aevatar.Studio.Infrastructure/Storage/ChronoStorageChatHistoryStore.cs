using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ChronoStorageChatHistoryStore : IChatHistoryStore
{
    private const string ChatHistoriesDir = "chat-histories";
    private const string MetaDir = $"{ChatHistoriesDir}/_meta";
    private const string LegacyIndexKey = $"{ChatHistoriesDir}/index.json";
    private const string NyxIdChatActorPrefix = "nyxid-chat-";
    private const string LegacyConversationPrefix = "conv-";

    private static readonly ChatHistoryIndex EmptyIndex = new([]);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ChronoStorageCatalogBlobClient _blobClient;
    private readonly ConnectorCatalogStorageOptions _options;
    private readonly ILogger<ChronoStorageChatHistoryStore> _logger;

    public ChronoStorageChatHistoryStore(
        ChronoStorageCatalogBlobClient blobClient,
        IOptions<ConnectorCatalogStorageOptions> options,
        ILogger<ChronoStorageChatHistoryStore> logger)
    {
        _blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ChatHistoryIndex> GetIndexAsync(string scopeId, CancellationToken ct = default)
    {
        try
        {
            var context = TryResolveChatHistoryScope(scopeId);
            if (context is null)
                return EmptyIndex;

            // Single list call for the entire chat-histories/ prefix.
            var objects = await _blobClient.ListObjectsAsync(context, ChatHistoriesDir, ct);
            if (objects.Objects.Count == 0)
                return EmptyIndex;

            // Partition into .jsonl conversation files and _meta/ sidecar files.
            var jsonlByConvId = new Dictionary<string, ChronoStorageCatalogBlobClient.StorageObject>(StringComparer.Ordinal);
            var metaByConvId = new Dictionary<string, ChronoStorageCatalogBlobClient.StorageObject>(StringComparer.Ordinal);

            foreach (var obj in objects.Objects)
            {
                if (IsMetaFile(obj.Key))
                {
                    var convId = TryExtractMetaConversationId(obj.Key);
                    if (convId is not null)
                        metaByConvId[convId] = obj;
                }
                else if (IsConversationFile(obj.Key))
                {
                    var convId = TryExtractConversationId(obj.Key);
                    if (convId is not null)
                        jsonlByConvId[convId] = obj;
                }
            }

            if (jsonlByConvId.Count == 0)
                return EmptyIndex;

            // For each conversation, prefer sidecar if fresh; fallback to full download.
            var tasks = new List<Task<ConversationMeta?>>(jsonlByConvId.Count);
            foreach (var (convId, jsonlObj) in jsonlByConvId)
            {
                if (metaByConvId.TryGetValue(convId, out var metaObj) && !IsSidecarStale(metaObj, jsonlObj))
                    tasks.Add(TryReadSidecarMetaAsync(scopeId, convId, ct));
                else
                    tasks.Add(TryBuildAndBackfillMetaAsync(scopeId, convId, jsonlObj, ct));
            }

            var conversations = await Task.WhenAll(tasks);

            return new ChatHistoryIndex(conversations
                .Where(static c => c is not null)
                .Select(static c => c!)
                .OrderByDescending(static c => c.UpdatedAt)
                .ThenBy(static c => c.Id, StringComparer.Ordinal)
                .ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read chat history index for scope {ScopeId}", scopeId);
            return EmptyIndex;
        }
    }

    public async Task<IReadOnlyList<StoredChatMessage>> GetMessagesAsync(
        string scopeId, string conversationId, CancellationToken ct = default)
    {
        try
        {
            var context = TryResolveMessages(scopeId, conversationId);
            if (context is null)
                return [];

            var payload = await _blobClient.TryDownloadAsync(context, ct);
            if (payload is null)
                return [];

            return DeserializeJsonl(payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read messages for conversation {ConversationId}", conversationId);
            return [];
        }
    }

    public async Task SaveMessagesAsync(
        string scopeId, string conversationId, IReadOnlyList<StoredChatMessage> messages, CancellationToken ct = default)
    {
        var context = TryResolveMessages(scopeId, conversationId)
            ?? throw new InvalidOperationException("Chat history storage is not available.");

        var jsonl = SerializeJsonl(messages);
        await _blobClient.UploadAsync(context, jsonl, "application/x-ndjson", ct);

        // Best-effort sidecar write after .jsonl succeeds.
        try
        {
            await WriteSidecarAsync(scopeId, conversationId, messages, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write sidecar for conversation {ConversationId}", conversationId);
        }

        await DeleteLegacyIndexIfExistsAsync(scopeId, ct);
    }

    public async Task DeleteConversationAsync(string scopeId, string conversationId, CancellationToken ct = default)
    {
        // Delete sidecar first, then .jsonl — avoids orphan sidecar pointing to deleted conversation.
        var metaContext = TryResolveSidecar(scopeId, conversationId);
        if (metaContext is not null)
        {
            try { await _blobClient.DeleteIfExistsAsync(metaContext, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to delete sidecar for conversation {ConversationId}", conversationId);
            }
        }

        var messagesContext = TryResolveMessages(scopeId, conversationId);
        if (messagesContext is not null)
        {
            await _blobClient.DeleteIfExistsAsync(messagesContext, ct);
        }
        await DeleteLegacyIndexIfExistsAsync(scopeId, ct);
    }

    private ChronoStorageCatalogBlobClient.RemoteScopeContext? TryResolveChatHistoryScope(string scopeId)
    {
        try
        {
            return _blobClient.TryResolveContext(
                _options.UserConfigPrefix,
                $"{ChatHistoriesDir}/.probe");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Chrono-storage context could not be resolved for chat history scope");
            return null;
        }
    }

    private ChronoStorageCatalogBlobClient.RemoteScopeContext? TryResolveMessages(string scopeId, string conversationId)
    {
        try
        {
            return _blobClient.TryResolveContext(
                _options.UserConfigPrefix,
                $"{ChatHistoriesDir}/{conversationId}.jsonl");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Chrono-storage context could not be resolved for conversation {ConversationId}", conversationId);
            return null;
        }
    }

    // ── Sidecar read/write helpers ──────────────────────────────────────

    private sealed record SidecarPayload(
        string Title,
        string ServiceId,
        string ServiceKind,
        long CreatedAtMs,
        long UpdatedAtMs,
        int MessageCount);

    private async Task<ConversationMeta?> TryReadSidecarMetaAsync(
        string scopeId, string conversationId, CancellationToken ct)
    {
        var context = TryResolveSidecar(scopeId, conversationId);
        if (context is null)
            return null;

        var payload = await _blobClient.TryDownloadAsync(context, ct);
        if (payload is null)
            return null;

        try
        {
            var sidecar = JsonSerializer.Deserialize<SidecarPayload>(payload, JsonOptions);
            if (sidecar is null)
                return null;

            return new ConversationMeta(
                Id: conversationId,
                Title: sidecar.Title,
                ServiceId: sidecar.ServiceId,
                ServiceKind: sidecar.ServiceKind,
                CreatedAt: FromUnixTimestampMilliseconds(sidecar.CreatedAtMs),
                UpdatedAt: FromUnixTimestampMilliseconds(sidecar.UpdatedAtMs),
                MessageCount: sidecar.MessageCount);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed sidecar for conversation {ConversationId}", conversationId);
            return null;
        }
    }

    /// <summary>
    /// Fallback: download full .jsonl, build meta, and best-effort write sidecar for next time.
    /// </summary>
    private async Task<ConversationMeta?> TryBuildAndBackfillMetaAsync(
        string scopeId,
        string conversationId,
        ChronoStorageCatalogBlobClient.StorageObject jsonlObj,
        CancellationToken ct)
    {
        var meta = await TryBuildConversationMetaFromJsonl(scopeId, conversationId, jsonlObj, ct);
        if (meta is null)
            return null;

        // Best-effort backfill — don't block the response on this.
        _ = Task.Run(async () =>
        {
            try
            {
                var messagesContext = TryResolveMessages(scopeId, conversationId);
                if (messagesContext is null) return;
                var bytes = await _blobClient.TryDownloadAsync(messagesContext, ct);
                if (bytes is null) return;
                var messages = DeserializeJsonl(bytes);
                await WriteSidecarAsync(scopeId, conversationId, messages, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Best-effort sidecar backfill failed for {ConversationId}", conversationId);
            }
        }, ct);

        return meta;
    }

    private async Task<ConversationMeta?> TryBuildConversationMetaFromJsonl(
        string scopeId,
        string conversationId,
        ChronoStorageCatalogBlobClient.StorageObject storageObject,
        CancellationToken ct)
    {
        var context = TryResolveMessages(scopeId, conversationId);
        if (context is null)
            return null;

        var payload = await _blobClient.TryDownloadAsync(context, ct);
        if (payload is null)
            return null;

        var messages = DeserializeJsonl(payload);
        return BuildMetaFromMessages(scopeId, conversationId, messages, storageObject);
    }

    private static ConversationMeta BuildMetaFromMessages(
        string scopeId,
        string conversationId,
        IReadOnlyList<StoredChatMessage> messages,
        ChronoStorageCatalogBlobClient.StorageObject? storageObject)
    {
        if (messages.Count == 0)
        {
            var fallbackTimestamp = (storageObject is not null ? TryParseStorageTimestamp(storageObject.LastModified) : null)
                ?? DateTimeOffset.UtcNow;
            var (fsi, fsk) = InferConversationService(scopeId, conversationId);
            return new ConversationMeta(conversationId, conversationId, fsi, fsk, fallbackTimestamp, fallbackTimestamp, 0);
        }

        var title = BuildConversationTitle(messages, conversationId);
        var defaultTs = (storageObject is not null ? TryParseStorageTimestamp(storageObject.LastModified) : null)
            ?? DateTimeOffset.UtcNow;
        var createdAt = messages.Select(static m => FromUnixTimestampMilliseconds(m.Timestamp)).DefaultIfEmpty(defaultTs).Min();
        var updatedAt = messages.Select(static m => FromUnixTimestampMilliseconds(m.Timestamp)).DefaultIfEmpty(createdAt).Max();
        var (serviceId, serviceKind) = InferConversationService(scopeId, conversationId);
        return new ConversationMeta(conversationId, title, serviceId, serviceKind, createdAt, updatedAt, messages.Count);
    }

    private async Task WriteSidecarAsync(
        string scopeId, string conversationId,
        IReadOnlyList<StoredChatMessage> messages, CancellationToken ct)
    {
        var context = TryResolveSidecar(scopeId, conversationId);
        if (context is null) return;

        var meta = BuildMetaFromMessages(scopeId, conversationId, messages, storageObject: null);
        var sidecar = new SidecarPayload(
            meta.Title,
            meta.ServiceId,
            meta.ServiceKind,
            meta.CreatedAt.ToUnixTimeMilliseconds(),
            meta.UpdatedAt.ToUnixTimeMilliseconds(),
            meta.MessageCount);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(sidecar, JsonOptions);
        await _blobClient.UploadAsync(context, bytes, "application/json", ct);
    }

    private static bool IsSidecarStale(
        ChronoStorageCatalogBlobClient.StorageObject metaObj,
        ChronoStorageCatalogBlobClient.StorageObject jsonlObj)
    {
        var metaTs = TryParseStorageTimestamp(metaObj.LastModified);
        var jsonlTs = TryParseStorageTimestamp(jsonlObj.LastModified);
        if (metaTs is null || jsonlTs is null)
            return true; // Can't compare — treat as stale.
        return metaTs.Value < jsonlTs.Value;
    }

    private ChronoStorageCatalogBlobClient.RemoteScopeContext? TryResolveSidecar(string scopeId, string conversationId)
    {
        try
        {
            return _blobClient.TryResolveContext(
                _options.UserConfigPrefix,
                $"{MetaDir}/{conversationId}.json");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Chrono-storage context could not be resolved for sidecar {ConversationId}", conversationId);
            return null;
        }
    }

    private static bool IsMetaFile(string key) =>
        key.StartsWith($"{MetaDir}/", StringComparison.Ordinal) &&
        key.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<StoredChatMessage> DeserializeJsonl(byte[] payload)
    {
        var text = Encoding.UTF8.GetString(payload);
        var messages = new List<StoredChatMessage>();

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var msg = JsonSerializer.Deserialize<StoredChatMessage>(line, JsonOptions);
            if (msg is not null)
                messages.Add(msg);
        }

        return messages;
    }

    private static byte[] SerializeJsonl(IReadOnlyList<StoredChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            sb.AppendLine(JsonSerializer.Serialize(msg, JsonOptions));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private async Task DeleteLegacyIndexIfExistsAsync(string scopeId, CancellationToken ct)
    {
        var context = TryResolveLegacyIndex(scopeId);
        if (context is null)
            return;

        try
        {
            await _blobClient.DeleteIfExistsAsync(context, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to delete legacy chat history index for scope {ScopeId}", scopeId);
        }
    }

    private ChronoStorageCatalogBlobClient.RemoteScopeContext? TryResolveLegacyIndex(string scopeId)
    {
        try
        {
            return _blobClient.TryResolveContext(_options.UserConfigPrefix, LegacyIndexKey);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Chrono-storage context could not be resolved for legacy chat history index");
            return null;
        }
    }

    private static bool IsConversationFile(string key) =>
        key.StartsWith($"{ChatHistoriesDir}/", StringComparison.Ordinal) &&
        key.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);

    private static string? TryExtractConversationId(string key)
    {
        if (!IsConversationFile(key))
            return null;

        var fileName = key[(ChatHistoriesDir.Length + 1)..];
        return fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".jsonl".Length]
            : null;
    }

    private static string? TryExtractMetaConversationId(string key)
    {
        if (!IsMetaFile(key))
            return null;

        var fileName = key[(MetaDir.Length + 1)..];
        return fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".json".Length]
            : null;
    }

    private static string BuildConversationTitle(IReadOnlyList<StoredChatMessage> messages, string fallback)
    {
        var source = messages
            .FirstOrDefault(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(message.Content))
            ?.Content
            ?? messages.FirstOrDefault(static message => !string.IsNullOrWhiteSpace(message.Content))?.Content
            ?? fallback;
        var trimmed = source.Trim();
        return trimmed.Length <= 60 ? trimmed : trimmed[..60];
    }

    private static (string ServiceId, string ServiceKind) InferConversationService(string scopeId, string conversationId)
    {
        if (conversationId.StartsWith("NyxIdChat:", StringComparison.OrdinalIgnoreCase) ||
            conversationId.StartsWith(NyxIdChatActorPrefix, StringComparison.OrdinalIgnoreCase) ||
            conversationId.StartsWith(LegacyConversationPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ("nyxid-chat", "nyxid-chat");
        }

        var separatorIndex = conversationId.IndexOf(':');
        if (separatorIndex > 0)
        {
            var serviceId = conversationId[..separatorIndex];
            var isNyxIdChat = string.Equals(serviceId, "nyxid-chat", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(conversationId, $"NyxIdChat:{scopeId}", StringComparison.OrdinalIgnoreCase);
            return (serviceId, isNyxIdChat ? "nyxid-chat" : "service");
        }

        return ("nyxid-chat", "nyxid-chat");
    }

    private static DateTimeOffset FromUnixTimestampMilliseconds(long timestamp) =>
        timestamp > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
            : DateTimeOffset.UnixEpoch;

    private static DateTimeOffset? TryParseStorageTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : null;
}
