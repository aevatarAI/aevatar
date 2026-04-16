using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IChatHistoryStore"/>.
/// Reads from the projection document store (CQRS read model).
/// Writes send commands only to <see cref="ChatConversationGAgent"/>
/// (index updates are handled internally by the conversation actor).
/// </summary>
internal sealed class ActorBackedChatHistoryStore : IChatHistoryStore
{
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IProjectionDocumentReader<ChatHistoryIndexCurrentStateDocument, string> _indexDocumentReader;
    private readonly IProjectionDocumentReader<ChatConversationCurrentStateDocument, string> _conversationDocumentReader;
    private readonly StudioProjectionPort _projectionPort;
    private readonly ILogger<ActorBackedChatHistoryStore> _logger;

    public ActorBackedChatHistoryStore(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IProjectionDocumentReader<ChatHistoryIndexCurrentStateDocument, string> indexDocumentReader,
        IProjectionDocumentReader<ChatConversationCurrentStateDocument, string> conversationDocumentReader,
        StudioProjectionPort projectionPort,
        ILogger<ActorBackedChatHistoryStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _indexDocumentReader = indexDocumentReader ?? throw new ArgumentNullException(nameof(indexDocumentReader));
        _conversationDocumentReader = conversationDocumentReader ?? throw new ArgumentNullException(nameof(conversationDocumentReader));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ChatHistoryIndex> GetIndexAsync(string scopeId, CancellationToken ct = default)
    {
        var actorId = IndexActorId(scopeId);
        var document = await _indexDocumentReader.GetAsync(actorId, ct);
        if (document?.StateRoot == null ||
            !document.StateRoot.Is(ChatHistoryIndexState.Descriptor))
            return new ChatHistoryIndex([]);

        var state = document.StateRoot.Unpack<ChatHistoryIndexState>();
        return new ChatHistoryIndex(state.Conversations
            .Select(ToConversationMeta)
            .OrderByDescending(static c => c.UpdatedAt)
            .ThenBy(static c => c.Id, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly());
    }

    public async Task<IReadOnlyList<StoredChatMessage>> GetMessagesAsync(
        string scopeId, string conversationId, CancellationToken ct = default)
    {
        var actorId = ConversationActorId(scopeId, conversationId);
        var document = await _conversationDocumentReader.GetAsync(actorId, ct);
        if (document?.StateRoot == null ||
            !document.StateRoot.Is(ChatConversationState.Descriptor))
            return [];

        var state = document.StateRoot.Unpack<ChatConversationState>();
        if (state.Messages.Count == 0)
            return [];

        return state.Messages
            .Select(ToStoredChatMessage)
            .ToList()
            .AsReadOnly();
    }

    public async Task SaveMessagesAsync(
        string scopeId, string conversationId, ConversationMeta meta,
        IReadOnlyList<StoredChatMessage> messages, CancellationToken ct = default)
    {
        // Only send to conversation actor; it forwards to index actor internally
        var conversationActor = await EnsureConversationActorAsync(scopeId, conversationId, ct);
        var metaProto = ToConversationMetaProto(conversationId, meta);
        var replaceEvt = new MessagesReplacedEvent { Meta = metaProto, ScopeId = scopeId };
        foreach (var msg in messages)
            replaceEvt.Messages.Add(ToStoredChatMessageProto(msg));

        await ActorCommandDispatcher.SendAsync(_dispatchPort, conversationActor, replaceEvt, ct);
    }

    public async Task DeleteConversationAsync(
        string scopeId, string conversationId, CancellationToken ct = default)
    {
        // Only send to conversation actor; it forwards to index actor internally
        var conversationActor = await EnsureConversationActorAsync(scopeId, conversationId, ct);
        var deleteEvt = new ConversationDeletedEvent
        {
            ConversationId = conversationId,
            ScopeId = scopeId,
        };
        await ActorCommandDispatcher.SendAsync(_dispatchPort, conversationActor, deleteEvt, ct);
    }

    // ── Actor resolution ───────────────────────────────────────

    private async Task<IActor> EnsureConversationActorAsync(
        string scopeId, string conversationId, CancellationToken ct)
    {
        var actorId = ConversationActorId(scopeId, conversationId);
        var actor = await _runtime.GetAsync(actorId)
                    ?? await _runtime.CreateAsync<ChatConversationGAgent>(actorId, ct);
        // Activate both projections: the conversation's own state AND the
        // per-scope index actor that the conversation forwards events to
        // internally. Without this either projection, GET returns stale
        // data after saves.
        await _projectionPort.EnsureProjectionAsync(actorId, StudioProjectionKinds.ChatConversation, ct);
        await _projectionPort.EnsureProjectionAsync(IndexActorId(scopeId), StudioProjectionKinds.ChatHistoryIndex, ct);
        return actor;
    }

    // ── Actor ID conventions ───────────────────────────────────

    private static string IndexActorId(string scopeId) => $"chat-index-{scopeId}";
    private static string ConversationActorId(string scopeId, string conversationId) => $"chat-{scopeId}-{conversationId}";

    // ── Mapping helpers ────────────────────────────────────────

    private static ConversationMeta ToConversationMeta(ConversationMetaProto proto) =>
        new(
            Id: proto.Id,
            Title: proto.Title,
            ServiceId: proto.ServiceId,
            ServiceKind: proto.ServiceKind,
            CreatedAt: FromUnixMs(proto.CreatedAtMs),
            UpdatedAt: FromUnixMs(proto.UpdatedAtMs),
            MessageCount: proto.MessageCount,
            LlmRoute: string.IsNullOrEmpty(proto.LlmRoute) ? null : proto.LlmRoute,
            LlmModel: string.IsNullOrEmpty(proto.LlmModel) ? null : proto.LlmModel);

    private static ConversationMetaProto ToConversationMetaProto(string conversationId, ConversationMeta meta) =>
        new()
        {
            Id = conversationId,
            Title = meta.Title ?? string.Empty,
            ServiceId = meta.ServiceId ?? string.Empty,
            ServiceKind = meta.ServiceKind ?? string.Empty,
            CreatedAtMs = meta.CreatedAt.ToUnixTimeMilliseconds(),
            UpdatedAtMs = meta.UpdatedAt.ToUnixTimeMilliseconds(),
            MessageCount = meta.MessageCount,
            LlmRoute = meta.LlmRoute ?? string.Empty,
            LlmModel = meta.LlmModel ?? string.Empty,
        };

    private static StoredChatMessage ToStoredChatMessage(StoredChatMessageProto proto) =>
        new(
            Id: proto.Id,
            Role: proto.Role,
            Content: proto.Content,
            Timestamp: proto.Timestamp,
            Status: proto.Status,
            Error: string.IsNullOrEmpty(proto.Error) ? null : proto.Error,
            Thinking: string.IsNullOrEmpty(proto.Thinking) ? null : proto.Thinking);

    private static StoredChatMessageProto ToStoredChatMessageProto(StoredChatMessage msg) =>
        new()
        {
            Id = msg.Id ?? string.Empty,
            Role = msg.Role ?? string.Empty,
            Content = msg.Content ?? string.Empty,
            Timestamp = msg.Timestamp,
            Status = msg.Status ?? string.Empty,
            Error = msg.Error ?? string.Empty,
            Thinking = msg.Thinking ?? string.Empty,
        };

    private static DateTimeOffset FromUnixMs(long ms) =>
        ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.UnixEpoch;
}
