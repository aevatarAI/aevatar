using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IChatHistoryStore"/>.
/// Uses a dual-actor architecture:
/// <list type="bullet">
///   <item><see cref="ChatConversationGAgent"/>: per-conversation actor (actorId = chat-{conversationId})</item>
///   <item><see cref="ChatHistoryIndexGAgent"/>: per-user index actor (actorId = chat-index-{scopeId})</item>
/// </list>
/// Writes go through actor event handlers. Reads come from snapshots via event subscription.
/// </summary>
internal sealed class ActorBackedChatHistoryStore : IChatHistoryStore, IAsyncDisposable
{
    private static readonly ChatHistoryIndex EmptyIndex = new([]);

    private readonly IActorRuntime _runtime;
    private readonly IActorEventSubscriptionProvider _subscriptions;
    private readonly ILogger<ActorBackedChatHistoryStore> _logger;

    // Per-index-actor subscription tracking
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly Dictionary<string, IndexSubscription> _indexSubscriptions = new(StringComparer.Ordinal);

    // Per-conversation subscription tracking
    private readonly SemaphoreSlim _conversationLock = new(1, 1);
    private readonly Dictionary<string, ConversationSubscription> _conversationSubscriptions = new(StringComparer.Ordinal);

    public ActorBackedChatHistoryStore(
        IActorRuntime runtime,
        IActorEventSubscriptionProvider subscriptions,
        ILogger<ActorBackedChatHistoryStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ChatHistoryIndex> GetIndexAsync(string scopeId, CancellationToken ct = default)
    {
        var sub = await EnsureIndexSubscriptionAsync(scopeId, ct);
        var state = sub.Snapshot;
        if (state is null)
            return EmptyIndex;

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
        var sub = await EnsureConversationSubscriptionAsync(scopeId, conversationId, ct);
        var state = sub.Snapshot;
        if (state is null || state.Messages.Count == 0)
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
        // 1. Send messages to the conversation actor
        var conversationActor = await EnsureConversationActorAsync(scopeId, conversationId, ct);
        var metaProto = ToConversationMetaProto(conversationId, meta);
        var replaceEvt = new MessagesReplacedEvent { Meta = metaProto };
        foreach (var msg in messages)
            replaceEvt.Messages.Add(ToStoredChatMessageProto(msg));

        await SendCommandAsync(conversationActor, replaceEvt, ct);

        // 2. Update the index actor
        var indexActor = await EnsureIndexActorAsync(scopeId, ct);
        // Update message count from actual messages
        var indexMeta = metaProto.Clone();
        indexMeta.MessageCount = messages.Count;
        var upsertEvt = new ConversationUpsertedEvent { Meta = indexMeta };
        await SendCommandAsync(indexActor, upsertEvt, ct);
    }

    public async Task DeleteConversationAsync(
        string scopeId, string conversationId, CancellationToken ct = default)
    {
        // 1. Mark conversation deleted
        var conversationActor = await EnsureConversationActorAsync(scopeId, conversationId, ct);
        var deleteEvt = new ConversationDeletedEvent { ConversationId = conversationId };
        await SendCommandAsync(conversationActor, deleteEvt, ct);

        // 2. Remove from index
        var indexActor = await EnsureIndexActorAsync(scopeId, ct);
        var removeEvt = new ConversationRemovedEvent { ConversationId = conversationId };
        await SendCommandAsync(indexActor, removeEvt, ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sub in _indexSubscriptions.Values)
        {
            if (sub.Subscription is not null)
                await sub.Subscription.DisposeAsync();
        }
        _indexSubscriptions.Clear();

        foreach (var sub in _conversationSubscriptions.Values)
        {
            if (sub.Subscription is not null)
                await sub.Subscription.DisposeAsync();
        }
        _conversationSubscriptions.Clear();
    }

    // ── Index actor helpers ─────────────────────────────────────

    private async Task<IndexSubscription> EnsureIndexSubscriptionAsync(string scopeId, CancellationToken ct)
    {
        var actorId = IndexActorId(scopeId);

        if (_indexSubscriptions.TryGetValue(actorId, out var existing) && existing.Initialized)
            return existing;

        await _indexLock.WaitAsync(ct);
        try
        {
            if (_indexSubscriptions.TryGetValue(actorId, out existing) && existing.Initialized)
                return existing;

            var sub = new IndexSubscription();
            sub.Subscription = await _subscriptions.SubscribeAsync<EventEnvelope>(
                actorId,
                envelope => HandleIndexEventAsync(actorId, envelope),
                ct);

            await EnsureIndexActorAsync(scopeId, ct);
            sub.Initialized = true;
            _indexSubscriptions[actorId] = sub;
            return sub;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private Task HandleIndexEventAsync(string actorId, EventEnvelope envelope)
    {
        if (envelope.Payload is null)
            return Task.CompletedTask;

        if (envelope.Payload.Is(ChatHistoryIndexStateSnapshotEvent.Descriptor))
        {
            var snapshot = envelope.Payload.Unpack<ChatHistoryIndexStateSnapshotEvent>();
            if (_indexSubscriptions.TryGetValue(actorId, out var sub))
            {
                sub.Snapshot = snapshot.Snapshot;
                _logger.LogDebug("Chat history index updated for {ActorId}: {Count} conversations",
                    actorId, snapshot.Snapshot?.Conversations.Count ?? 0);
            }
        }

        return Task.CompletedTask;
    }

    private async Task<IActor> EnsureIndexActorAsync(string scopeId, CancellationToken ct)
    {
        var actorId = IndexActorId(scopeId);
        var actor = await _runtime.GetAsync(actorId);
        return actor ?? await _runtime.CreateAsync<ChatHistoryIndexGAgent>(actorId, ct);
    }

    // ── Conversation actor helpers ──────────────────────────────

    private async Task<ConversationSubscription> EnsureConversationSubscriptionAsync(
        string scopeId, string conversationId, CancellationToken ct)
    {
        var actorId = ConversationActorId(scopeId, conversationId);

        if (_conversationSubscriptions.TryGetValue(actorId, out var existing) && existing.Initialized)
            return existing;

        await _conversationLock.WaitAsync(ct);
        try
        {
            if (_conversationSubscriptions.TryGetValue(actorId, out existing) && existing.Initialized)
                return existing;

            var sub = new ConversationSubscription();
            sub.Subscription = await _subscriptions.SubscribeAsync<EventEnvelope>(
                actorId,
                envelope => HandleConversationEventAsync(actorId, envelope),
                ct);

            await EnsureConversationActorAsync(scopeId, conversationId, ct);
            sub.Initialized = true;
            _conversationSubscriptions[actorId] = sub;
            return sub;
        }
        finally
        {
            _conversationLock.Release();
        }
    }

    private Task HandleConversationEventAsync(string actorId, EventEnvelope envelope)
    {
        if (envelope.Payload is null)
            return Task.CompletedTask;

        if (envelope.Payload.Is(ChatConversationStateSnapshotEvent.Descriptor))
        {
            var snapshot = envelope.Payload.Unpack<ChatConversationStateSnapshotEvent>();
            if (_conversationSubscriptions.TryGetValue(actorId, out var sub))
            {
                sub.Snapshot = snapshot.Snapshot;
                _logger.LogDebug("Chat conversation updated for {ActorId}: {Count} messages",
                    actorId, snapshot.Snapshot?.Messages.Count ?? 0);
            }
        }

        return Task.CompletedTask;
    }

    private async Task<IActor> EnsureConversationActorAsync(string scopeId, string conversationId, CancellationToken ct)
    {
        var actorId = ConversationActorId(scopeId, conversationId);
        var actor = await _runtime.GetAsync(actorId);
        return actor ?? await _runtime.CreateAsync<ChatConversationGAgent>(actorId, ct);
    }

    // ── Actor ID conventions ────────────────────────────────────

    private static string IndexActorId(string scopeId) => $"chat-index-{scopeId}";
    private static string ConversationActorId(string scopeId, string conversationId) => $"chat-{scopeId}-{conversationId}";

    // ── Command dispatch ────────────────────────────────────────

    private static async Task SendCommandAsync(IActor actor, IMessage command, CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };
        await actor.HandleEventAsync(envelope, ct);
    }

    // ── Mapping helpers ─────────────────────────────────────────

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

    // ── Subscription tracking ───────────────────────────────────

    private sealed class IndexSubscription
    {
        public volatile ChatHistoryIndexState? Snapshot;
        public IAsyncDisposable? Subscription;
        public bool Initialized;
    }

    private sealed class ConversationSubscription
    {
        public volatile ChatConversationState? Snapshot;
        public IAsyncDisposable? Subscription;
        public bool Initialized;
    }
}
