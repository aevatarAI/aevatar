using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ActorBacked;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Studio.Tests;

public sealed class ActorBackedChatHistoryStoreTests
{
    [Fact]
    public async Task BlockedTurn_ShouldRoundTripTypedTurnIdentityAndStatus()
    {
        var actor = new StubActor("chat-history-conversation-scope-a-conversation-a");
        var dispatch = new RecordingDispatchService();
        var reader = new RecordingDocumentReader
        {
            Document = new ChatConversationCurrentStateDocument
            {
                ActorId = actor.Id,
                ScopeId = "scope-a",
                ConversationId = "conversation-a",
                Turns =
                {
                    new ChatConversationTurnDocument
                    {
                        TurnId = "turn-blocked",
                        Sequence = 1,
                        UserText = "read private resource",
                        TerminalStatus = "blocked",
                        SanitizedError = "Connect api-github to continue.",
                    },
                },
            },
        };
        var store = new ActorBackedChatHistoryStore(
            new RecordingBootstrap(actor),
            new StudioActorCommandDispatch(dispatch),
            reader,
            new RecordingCreateRecoveryDocumentReader());
        var now = DateTimeOffset.Parse("2026-07-20T08:00:00Z");
        var meta = new ConversationMeta(
            "conversation-a",
            "Private resource",
            "service-a",
            "nyxid-chat",
            now,
            now,
            2);

        await store.SaveMessagesAsync(
            "scope-a",
            "conversation-a",
            meta,
            [
                new StoredChatMessage(
                    "turn-blocked-user",
                    "user",
                    "read private resource",
                    now.ToUnixTimeMilliseconds(),
                    "completed",
                    TurnId: "turn-blocked"),
                new StoredChatMessage(
                    "turn-blocked-assistant",
                    "assistant",
                    string.Empty,
                    now.ToUnixTimeMilliseconds(),
                    "blocked",
                    Error: "Connect api-github to continue.",
                    TurnId: "turn-blocked"),
            ]);

        var append = dispatch.Payloads.Should().ContainSingle().Which.Should()
            .BeOfType<AppendChatTurnCommand>().Subject;
        append.Turn.TurnId.Should().Be("turn-blocked");
        append.Turn.TerminalStatus.Should().Be(ChatTurnTerminalStatus.Blocked);
        append.Turn.SanitizedError.Should().Be("Connect api-github to continue.");

        var messages = await store.GetMessagesAsync("scope-a", "conversation-a");
        messages.Should().HaveCount(2);
        messages.Should().OnlyContain(message => message.TurnId == "turn-blocked");
        messages.Select(static message => message.Id).Should()
            .Equal("turn-blocked:user", "turn-blocked:assistant");
        messages[1].Status.Should().Be("blocked");
        messages[1].Error.Should().Be("Connect api-github to continue.");
    }

    [Fact]
    public async Task GetMessagesAsync_ShouldReturnEmpty_WhenProjectedDocumentIdentityDoesNotMatchRequest()
    {
        var actorId = ChatHistoryActorIds.Conversation("scope-a", "conversation-a");
        var reader = new RecordingDocumentReader();
        reader.Seed(actorId, new ChatConversationCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            ScopeId = "scope-b",
            ConversationId = "conversation-a",
            Turns =
            {
                new ChatConversationTurnDocument
                {
                    TurnId = "turn-private",
                    Sequence = 1,
                    UserText = "private",
                    AssistantText = "secret",
                    TerminalStatus = "complete",
                },
            },
        });
        var store = new ActorBackedChatHistoryStore(
            new RecordingBootstrap(new StubActor(actorId)),
            new StudioActorCommandDispatch(new RecordingDispatchService()),
            reader,
            new RecordingCreateRecoveryDocumentReader());

        var messages = await store.GetMessagesAsync("scope-a", "conversation-a");

        messages.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteConversationAsync_ShouldNotDispatch_WhenProjectedDocumentIdentityDoesNotMatchRequest()
    {
        var actorId = ChatHistoryActorIds.Conversation("scope-a", "conversation-a");
        var reader = new RecordingDocumentReader();
        reader.Seed(actorId, new ChatConversationCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            ScopeId = "scope-b",
            ConversationId = "conversation-a",
            Deleted = false,
        });
        var dispatch = new RecordingDispatchService();
        var store = new ActorBackedChatHistoryStore(
            new RecordingBootstrap(new StubActor(actorId)),
            new StudioActorCommandDispatch(dispatch),
            reader,
            new RecordingCreateRecoveryDocumentReader());

        await store.DeleteConversationAsync("scope-a", "conversation-a");

        dispatch.Payloads.Should().BeEmpty();
    }

    [Fact]
    public async Task GetIndexAsync_ShouldUseBoundedPageRequestCursorAndDeterministicTieOrdering()
    {
        var reader = new RecordingDocumentReader
        {
            QueryResult = new ProjectionDocumentQueryResult<ChatConversationCurrentStateDocument>
            {
                Items =
                [
                    new ChatConversationCurrentStateDocument
                    {
                        Id = "actor-a",
                        ActorId = "actor-a",
                        ScopeId = "scope-a",
                        ConversationId = "conversation-a",
                        UpdatedAtMs = 100,
                    },
                    new ChatConversationCurrentStateDocument
                    {
                        Id = "actor-b",
                        ActorId = "actor-b",
                        ScopeId = "scope-a",
                        ConversationId = "conversation-b",
                        UpdatedAtMs = 100,
                    },
                ],
                NextCursor = "opaque-next",
            },
        };
        var store = new ActorBackedChatHistoryStore(
            new RecordingBootstrap(new StubActor("actor-a")),
            new StudioActorCommandDispatch(new RecordingDispatchService()),
            reader,
            new RecordingCreateRecoveryDocumentReader());

        var index = await store.GetIndexAsync(new ChatHistoryPageRequest(
            ScopeId: "scope-a",
            Take: 2,
            Cursor: "opaque-current"));

        index.Conversations.Select(static conversation => conversation.Id)
            .Should()
            .Equal("conversation-a", "conversation-b");
        index.NextCursor.Should().Be("opaque-next");
        reader.LastQuery.Should().NotBeNull();
        reader.LastQuery!.Take.Should().Be(2);
        reader.LastQuery.Cursor.Should().Be("opaque-current");
        reader.LastQuery.Sorts.Select(static sort => (sort.FieldPath, sort.Direction))
            .Should()
            .Equal(
                ("updated_at_ms", ProjectionDocumentSortDirection.Desc),
                ("conversation_id", ProjectionDocumentSortDirection.Asc));
    }

    [Fact]
    public async Task GetCreateRecoveryAsync_ShouldResolveScopeBoundMaterializedRecord()
    {
        var recoveryReader = new RecordingCreateRecoveryDocumentReader
        {
            QueryResult = new ProjectionDocumentQueryResult<ChatCreateRecoveryCurrentStateDocument>
            {
                Items =
                [
                    new ChatCreateRecoveryCurrentStateDocument
                    {
                        Id = "delivery-actor",
                        ActorId = "delivery-actor",
                        ScopeId = "scope-a",
                        CreateIdempotencyKey = "create-alpha",
                        ConversationId = "conversation-a",
                        TurnId = "turn-a",
                        Status = "append_committed",
                        SourceVersion = 4,
                        DeliveryActorId = "delivery-actor",
                    },
                ],
            },
        };
        var store = new ActorBackedChatHistoryStore(
            new RecordingBootstrap(new StubActor("actor-a")),
            new StudioActorCommandDispatch(new RecordingDispatchService()),
            new RecordingDocumentReader(),
            recoveryReader);

        var recovery = await store.GetCreateRecoveryAsync(new ChatCreateRecoveryRequest(
            ScopeId: "scope-a",
            CreateIdempotencyKey: "create-alpha"));

        recovery.Should().BeEquivalentTo(new ChatCreateRecovery(
            ConversationId: "conversation-a",
            TurnId: "turn-a",
            Status: "append_committed",
            SourceVersion: 4));
        recoveryReader.LastQuery.Should().NotBeNull();
        recoveryReader.LastQuery!.Filters.Should().Contain(filter =>
            filter.FieldPath == "scope_id" &&
            filter.Operator == ProjectionDocumentFilterOperator.Eq);
        recoveryReader.LastQuery.Filters.Should().Contain(filter =>
            filter.FieldPath == "create_idempotency_key" &&
            filter.Operator == ProjectionDocumentFilterOperator.Eq);
    }

    private sealed class RecordingDispatchService
        : ICommandDispatchService<StudioActorCommand, StudioActorCommandReceipt, StudioActorCommandStartError>
    {
        public List<IMessage> Payloads { get; } = [];

        public Task<CommandDispatchResult<StudioActorCommandReceipt, StudioActorCommandStartError>> DispatchAsync(
            StudioActorCommand command,
            CancellationToken ct = default)
        {
            Payloads.Add(command.Payload);
            return Task.FromResult(
                CommandDispatchResult<StudioActorCommandReceipt, StudioActorCommandStartError>.Success(
                    new StudioActorCommandReceipt(command.Actor.Id, "command-1", "correlation-1")));
        }
    }

    private sealed class RecordingDocumentReader
        : IProjectionDocumentReader<ChatConversationCurrentStateDocument, string>
    {
        private readonly Dictionary<string, ChatConversationCurrentStateDocument> _documents = new(StringComparer.Ordinal);
        public ChatConversationCurrentStateDocument? Document { get; init; }
        public ProjectionDocumentQueryResult<ChatConversationCurrentStateDocument> QueryResult { get; init; } =
            ProjectionDocumentQueryResult<ChatConversationCurrentStateDocument>.Empty;
        public ProjectionDocumentQuery? LastQuery { get; private set; }

        public void Seed(string key, ChatConversationCurrentStateDocument document) =>
            _documents[key] = document;

        public Task<ChatConversationCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            if (Document != null)
                return Task.FromResult<ChatConversationCurrentStateDocument?>(Document);

            return Task.FromResult(_documents.GetValueOrDefault(key));
        }

        public Task<ProjectionDocumentQueryResult<ChatConversationCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(QueryResult);
        }
    }

    private sealed class RecordingCreateRecoveryDocumentReader
        : IProjectionDocumentReader<ChatCreateRecoveryCurrentStateDocument, string>
    {
        public ProjectionDocumentQueryResult<ChatCreateRecoveryCurrentStateDocument> QueryResult { get; init; } =
            ProjectionDocumentQueryResult<ChatCreateRecoveryCurrentStateDocument>.Empty;
        public ProjectionDocumentQuery? LastQuery { get; private set; }

        public Task<ChatCreateRecoveryCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default) =>
            Task.FromResult<ChatCreateRecoveryCurrentStateDocument?>(null);

        public Task<ProjectionDocumentQueryResult<ChatCreateRecoveryCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(QueryResult);
        }
    }

    private sealed class RecordingBootstrap(IActor actor) : IStudioActorBootstrap
    {
        public Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor =>
            Task.FromResult(actor);
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new StubAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent : IAgent
    {
        public string Id => "stub-agent";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub-agent");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);
    }
}
