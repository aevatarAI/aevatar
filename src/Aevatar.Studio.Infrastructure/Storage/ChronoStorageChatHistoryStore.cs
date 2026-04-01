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

            var objects = await _blobClient.ListObjectsAsync(context, ChatHistoriesDir, ct);
            if (objects.Objects.Count == 0)
                return EmptyIndex;

            var conversations = await Task.WhenAll(objects.Objects
                .Where(static o => IsConversationFile(o.Key))
                .Select(o => TryBuildConversationMetaAsync(scopeId, o, ct)));

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
        await DeleteLegacyIndexIfExistsAsync(scopeId, ct);
    }

    public async Task DeleteConversationAsync(string scopeId, string conversationId, CancellationToken ct = default)
    {
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

    private async Task<ConversationMeta?> TryBuildConversationMetaAsync(
        string scopeId,
        ChronoStorageCatalogBlobClient.StorageObject storageObject,
        CancellationToken ct)
    {
        var conversationId = TryExtractConversationId(storageObject.Key);
        if (string.IsNullOrWhiteSpace(conversationId))
            return null;

        var context = TryResolveMessages(scopeId, conversationId);
        if (context is null)
            return null;

        var payload = await _blobClient.TryDownloadAsync(context, ct);
        if (payload is null)
            return null;

        var messages = DeserializeJsonl(payload);
        if (messages.Count == 0)
        {
            var fallbackTimestamp = TryParseStorageTimestamp(storageObject.LastModified) ?? DateTimeOffset.UtcNow;
            var (fallbackServiceId, fallbackServiceKind) = InferConversationService(scopeId, conversationId);
            return new ConversationMeta(
                Id: conversationId,
                Title: conversationId,
                ServiceId: fallbackServiceId,
                ServiceKind: fallbackServiceKind,
                CreatedAt: fallbackTimestamp,
                UpdatedAt: fallbackTimestamp,
                MessageCount: 0);
        }

        var title = BuildConversationTitle(messages, conversationId);
        var createdAt = messages
            .Select(static message => FromUnixTimestampMilliseconds(message.Timestamp))
            .DefaultIfEmpty(TryParseStorageTimestamp(storageObject.LastModified) ?? DateTimeOffset.UtcNow)
            .Min();
        var updatedAt = messages
            .Select(static message => FromUnixTimestampMilliseconds(message.Timestamp))
            .DefaultIfEmpty(createdAt)
            .Max();
        var (serviceId, serviceKind) = InferConversationService(scopeId, conversationId);

        return new ConversationMeta(
            Id: conversationId,
            Title: title,
            ServiceId: serviceId,
            ServiceKind: serviceKind,
            CreatedAt: createdAt,
            UpdatedAt: updatedAt,
            MessageCount: messages.Count);
    }

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
