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
    private const string IndexFileName = "index.json";

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
            var context = TryResolveIndex(scopeId);
            if (context is null)
                return EmptyIndex;

            var payload = await _blobClient.TryDownloadAsync(context, ct);
            if (payload is null)
                return EmptyIndex;

            return DeserializeIndex(payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read chat history index for scope {ScopeId}", scopeId);
            return EmptyIndex;
        }
    }

    public async Task SaveIndexAsync(string scopeId, ChatHistoryIndex index, CancellationToken ct = default)
    {
        var context = TryResolveIndex(scopeId)
            ?? throw new InvalidOperationException("Chat history storage is not available.");

        var json = JsonSerializer.SerializeToUtf8Bytes(index, JsonOptions);
        await _blobClient.UploadAsync(context, json, "application/json", ct);
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
    }

    public async Task DeleteConversationAsync(string scopeId, string conversationId, CancellationToken ct = default)
    {
        // Delete the JSONL file
        var messagesContext = TryResolveMessages(scopeId, conversationId);
        if (messagesContext is not null)
        {
            await _blobClient.DeleteIfExistsAsync(messagesContext, ct);
        }

        // Remove from index
        var indexContext = TryResolveIndex(scopeId);
        if (indexContext is null)
            return;

        var payload = await _blobClient.TryDownloadAsync(indexContext, ct);
        if (payload is null)
            return;

        var index = DeserializeIndex(payload);
        var updated = index.Conversations
            .Where(c => !string.Equals(c.Id, conversationId, StringComparison.Ordinal))
            .ToList();

        var newIndex = new ChatHistoryIndex(updated);
        var json = JsonSerializer.SerializeToUtf8Bytes(newIndex, JsonOptions);
        await _blobClient.UploadAsync(indexContext, json, "application/json", ct);
    }

    private ChronoStorageCatalogBlobClient.RemoteScopeContext? TryResolveIndex(string scopeId)
    {
        try
        {
            return _blobClient.TryResolveContext(
                _options.UserConfigPrefix,
                $"{ChatHistoriesDir}/{IndexFileName}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Chrono-storage context could not be resolved for chat history index");
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

    private static ChatHistoryIndex DeserializeIndex(byte[] payload)
    {
        var doc = JsonDocument.Parse(payload);
        var conversations = new List<ConversationMeta>();

        if (doc.RootElement.TryGetProperty("conversations", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                conversations.Add(new ConversationMeta(
                    Id: el.GetProperty("id").GetString() ?? string.Empty,
                    Title: el.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                    ServiceId: el.TryGetProperty("serviceId", out var si) ? si.GetString() ?? string.Empty : string.Empty,
                    ServiceKind: el.TryGetProperty("serviceKind", out var sk) ? sk.GetString() ?? string.Empty : string.Empty,
                    CreatedAt: el.TryGetProperty("createdAt", out var ca) ? ca.GetDateTimeOffset() : default,
                    UpdatedAt: el.TryGetProperty("updatedAt", out var ua) ? ua.GetDateTimeOffset() : default,
                    MessageCount: el.TryGetProperty("messageCount", out var mc) ? mc.GetInt32() : 0));
            }
        }

        return new ChatHistoryIndex(conversations);
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
}
