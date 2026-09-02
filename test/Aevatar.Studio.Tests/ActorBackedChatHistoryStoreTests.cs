using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ActorBacked;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class ActorBackedChatHistoryStoreTests
{
    [Fact]
    public async Task ReserveTurnDeliveryAsync_ShouldEnsureDeterministicActorAndDispatchSourceReservation()
    {
        var deliveryId = "delivery-alpha";
        var deliveryActorId = ChatTurnHistoryDeliveryActorIds.FromDeliveryId(deliveryId);
        var bootstrap = new RecordingBootstrap(new StubActor(deliveryActorId));
        var dispatch = new RecordingDispatchService();
        var store = new ActorBackedChatHistoryStore(
            bootstrap,
            new StudioActorCommandDispatch(dispatch),
            new RecordingDocumentReader(),
            new RecordingDeliveryDocumentReader());

        await store.ReserveTurnDeliveryAsync(new ChatHistoryTurnDeliveryReservation(
            deliveryId,
            " scope-a ",
            " conversation-a ",
            " turn-a ",
            " original user text ",
            " nyxid-conversation-a ",
            " command-a ",
            " correlation-a ",
            " fingerprint-a ",
            CreateConversationIfMissing: true,
            ExposeCreateRecovery: false));

        bootstrap.ActorIds.Should().ContainSingle(deliveryActorId);
        var command = dispatch.Commands.Should().ContainSingle().Which.Payload.Should()
            .BeOfType<ChatTurnHistoryDeliveryReserveRequested>().Subject;
        command.DeliveryId.Should().Be(deliveryId);
        command.ScopeId.Should().Be("scope-a");
        command.ConversationId.Should().Be("conversation-a");
        command.TurnId.Should().Be("turn-a");
        command.UserText.Should().Be("original user text");
        command.SourceActorId.Should().Be("nyxid-conversation-a");
        command.SourceCommandId.Should().Be("command-a");
        command.SourceCorrelationId.Should().Be("correlation-a");
        command.RequestFingerprint.Should().Be("fingerprint-a");
        command.CreateConversationIfMissing.Should().BeTrue();
        command.ExposeCreateRecovery.Should().BeFalse();
    }

    [Theory]
    [InlineData(ChatHistoryTurnTerminalStatus.Completed, ChatTurnTerminalStatus.Completed)]
    [InlineData(ChatHistoryTurnTerminalStatus.Failed, ChatTurnTerminalStatus.Failed)]
    [InlineData(ChatHistoryTurnTerminalStatus.Stopped, ChatTurnTerminalStatus.Stopped)]
    [InlineData(ChatHistoryTurnTerminalStatus.Blocked, ChatTurnTerminalStatus.Blocked)]
    [InlineData(ChatHistoryTurnTerminalStatus.OutcomeUncertain, ChatTurnTerminalStatus.OutcomeUncertain)]
    public async Task NotifyTurnTerminalAsync_ShouldUseSourcePublisherAndMapStatus(
        ChatHistoryTurnTerminalStatus status,
        ChatTurnTerminalStatus expectedStatus)
    {
        var deliveryId = "delivery-alpha";
        var deliveryActorId = ChatTurnHistoryDeliveryActorIds.FromDeliveryId(deliveryId);
        var bootstrap = new RecordingBootstrap(new StubActor(deliveryActorId));
        var dispatch = new RecordingDispatchService();
        var store = new ActorBackedChatHistoryStore(
            bootstrap,
            new StudioActorCommandDispatch(dispatch),
            new RecordingDocumentReader(),
            new RecordingDeliveryDocumentReader());
        var observedAt = DateTimeOffset.Parse("2026-07-28T02:03:04Z");

        await store.NotifyTurnTerminalAsync(new ChatHistoryTurnTerminalNotification(
            deliveryId,
            " nyxid-conversation-a ",
            " command-a ",
            status,
            " safe terminal text ",
            " safe_error_code ",
            observedAt));

        bootstrap.ActorIds.Should().ContainSingle(deliveryActorId);
        var dispatched = dispatch.Commands.Should().ContainSingle().Which;
        dispatched.PublisherId.Should().Be("nyxid-conversation-a");
        var command = dispatched.Payload.Should()
            .BeOfType<ChatTurnHistorySourceTerminalNotified>().Subject;
        command.DeliveryId.Should().Be(deliveryId);
        command.SourceActorId.Should().Be("nyxid-conversation-a");
        command.SourceCommandId.Should().Be("command-a");
        command.Status.Should().Be(expectedStatus);
        command.Text.Should().Be("safe terminal text");
        command.ErrorCode.Should().Be("safe_error_code");
        command.ObservedAtUnixMs.Should().Be(observedAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task InitializeConversationAsync_ShouldEnsureDeterministicActorAndDispatchTypedCommand()
    {
        var actorId = ChatHistoryActorIds.Conversation("scope-a", "conversation-a");
        var bootstrap = new RecordingBootstrap(new StubActor(actorId));
        var dispatch = new RecordingDispatchService();
        var store = new ActorBackedChatHistoryStore(
            bootstrap,
            new StudioActorCommandDispatch(dispatch),
            new RecordingDocumentReader(),
            new RecordingDeliveryDocumentReader());
        var createdAt = DateTimeOffset.Parse("2026-07-28T01:02:03Z");

        await store.InitializeConversationAsync(new ChatHistoryConversationInitialization(
            " initialize-1 ",
            " scope-a ",
            " conversation-a ",
            " service-a ",
            " nyxid.chat ",
            createdAt,
            " Initial title "));

        bootstrap.ActorIds.Should().ContainSingle(actorId);
        var command = dispatch.Payloads.Should().ContainSingle().Which.Should()
            .BeOfType<InitializeChatConversationCommand>().Subject;
        command.OperationId.Should().Be("initialize-1");
        command.ScopeId.Should().Be("scope-a");
        command.ConversationId.Should().Be("conversation-a");
        command.ServiceId.Should().Be("service-a");
        command.ServiceKind.Should().Be("nyxid.chat");
        command.CreatedAt.ToDateTimeOffset().Should().Be(createdAt);
        command.InitialTitle.Should().Be("Initial title");
    }

    [Fact]
    public async Task GetMessagesAsync_WithInitializedZeroTurnDocument_ShouldReturnFoundEmpty()
    {
        var actorId = ChatHistoryActorIds.Conversation("scope-a", "conversation-a");
        var reader = new RecordingDocumentReader();
        reader.Documents[actorId] = new ChatConversationCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            StateVersion = 1,
        };
        var store = new ActorBackedChatHistoryStore(
            new RecordingBootstrap(new StubActor(actorId)),
            new StudioActorCommandDispatch(new RecordingDispatchService()),
            reader,
            new RecordingDeliveryDocumentReader());

        var result = await store.GetMessagesAsync("scope-a", "conversation-a");

        result.Status.Should().Be(ChatHistoryConversationResultStatus.Found);
        result.StateVersion.Should().Be(1);
        result.Messages.Should().BeEmpty();
    }

    [Fact]
    public void ConversationActorId_ShouldEncodeTupleWithoutDelimiterCollision()
    {
        var first = ChatHistoryActorIds.Conversation("tenant", "admin-c1");
        var second = ChatHistoryActorIds.Conversation("tenant-admin", "c1");

        first.Should().NotBe(second);
        first.Should().StartWith("chat-conversation:");
        second.Should().StartWith("chat-conversation:");
    }

    [Fact]
    public async Task BlockedTurn_ShouldRoundTripTypedTurnIdentityAndStatus()
    {
        var actorId = ChatHistoryActorIds.Conversation("scope-a", "conversation-a");
        var actor = new StubActor(actorId);
        var dispatch = new RecordingDispatchService();
        var reader = new RecordingDocumentReader
        {
            Documents =
            {
                [actorId] = new ChatConversationCurrentStateDocument
                {
                    Id = actorId,
                    ActorId = actor.Id,
                    ScopeId = "scope-a",
                    ConversationId = "conversation-a",
                    StateVersion = 7,
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
                }
            }
        };
        var store = new ActorBackedChatHistoryStore(
            new RecordingBootstrap(actor),
            new StudioActorCommandDispatch(dispatch),
            reader,
            new RecordingDeliveryDocumentReader());
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

        var messagesResult = await store.GetMessagesAsync("scope-a", "conversation-a");
        messagesResult.Status.Should().Be(ChatHistoryConversationResultStatus.Found);
        messagesResult.StateVersion.Should().Be(7);
        var messages = messagesResult.Messages;
        messages.Should().HaveCount(2);
        messages.Should().OnlyContain(message => message.TurnId == "turn-blocked");
        messages.Select(static message => message.Id).Should()
            .Equal("turn-blocked:user", "turn-blocked:assistant");
        messages[1].Status.Should().Be("blocked");
        messages[1].Error.Should().Be("Connect api-github to continue.");
    }

    [Fact]
    public async Task OutcomeUncertainTurn_ShouldRoundTripWithoutBecomingFailed()
    {
        var actorId = ChatHistoryActorIds.Conversation("scope-a", "conversation-a");
        var actor = new StubActor(actorId);
        var dispatch = new RecordingDispatchService();
        var reader = new RecordingDocumentReader
        {
            Documents =
            {
                [actorId] = new ChatConversationCurrentStateDocument
                {
                    Id = actorId,
                    ActorId = actor.Id,
                    ScopeId = "scope-a",
                    ConversationId = "conversation-a",
                    StateVersion = 8,
                    Turns =
                    {
                        new ChatConversationTurnDocument
                        {
                            TurnId = "turn-uncertain",
                            Sequence = 1,
                            UserText = "perform side effect",
                            AssistantText = "The outcome could not be confirmed.",
                            TerminalStatus = "outcome_uncertain",
                            SanitizedError = "SESSION_OUTCOME_UNCERTAIN",
                        },
                    },
                },
            },
        };
        var store = new ActorBackedChatHistoryStore(
            new RecordingBootstrap(actor),
            new StudioActorCommandDispatch(dispatch),
            reader,
            new RecordingDeliveryDocumentReader());
        var now = DateTimeOffset.Parse("2026-08-02T08:00:00Z");

        await store.SaveMessagesAsync(
            "scope-a",
            "conversation-a",
            new ConversationMeta(
                "conversation-a",
                "Uncertain operation",
                "service-a",
                "nyxid-chat",
                now,
                now,
                2),
            [
                new StoredChatMessage(
                    "turn-uncertain-user",
                    "user",
                    "perform side effect",
                    now.ToUnixTimeMilliseconds(),
                    "completed",
                    TurnId: "turn-uncertain"),
                new StoredChatMessage(
                    "turn-uncertain-assistant",
                    "assistant",
                    "The outcome could not be confirmed.",
                    now.ToUnixTimeMilliseconds(),
                    "outcome_uncertain",
                    Error: "SESSION_OUTCOME_UNCERTAIN",
                    TurnId: "turn-uncertain"),
            ]);

        var append = dispatch.Payloads.Should().ContainSingle().Which.Should()
            .BeOfType<AppendChatTurnCommand>().Subject;
        append.Turn.TerminalStatus.Should().Be(ChatTurnTerminalStatus.OutcomeUncertain);
        var messages = (await store.GetMessagesAsync("scope-a", "conversation-a")).Messages;
        messages[1].Status.Should().Be("outcome_uncertain");
        messages[1].Error.Should().Be("SESSION_OUTCOME_UNCERTAIN");
    }

    [Fact]
    public async Task GetMessagesAsync_ShouldRejectLegacyCollisionDocument_WhenStoredTupleDoesNotMatchRequest()
    {
        var legacyActorId = ChatHistoryActorIds.LegacyConversation("tenant", "admin-c1");
        var reader = new RecordingDocumentReader();
        reader.Documents[legacyActorId] = new ChatConversationCurrentStateDocument
        {
            Id = legacyActorId,
            ActorId = legacyActorId,
            ScopeId = "tenant",
            ConversationId = "admin-c1",
            Turns =
            {
                new ChatConversationTurnDocument
                {
                    TurnId = "turn-owned",
                    Sequence = 1,
                    UserText = "owned",
                    AssistantText = "secret",
                    TerminalStatus = "complete",
                },
            },
        };
        var store = new ActorBackedChatHistoryStore(
            new RecordingBootstrap(new StubActor("unused")),
            new StudioActorCommandDispatch(new RecordingDispatchService()),
            reader,
            new RecordingDeliveryDocumentReader());

        var result = await store.GetMessagesAsync("tenant-admin", "c1");

        result.Status.Should().Be(ChatHistoryConversationResultStatus.NotFound);
        result.Messages.Should().BeEmpty();
        reader.GetKeys.Should().Equal(
            ChatHistoryActorIds.Conversation("tenant-admin", "c1"),
            ChatHistoryActorIds.LegacyConversation("tenant-admin", "c1"));
    }

    [Fact]
    public async Task DeleteConversationAsync_ShouldNotDispatch_WhenProjectedTupleDoesNotMatchRequest()
    {
        var legacyActorId = ChatHistoryActorIds.LegacyConversation("tenant", "admin-c1");
        var reader = new RecordingDocumentReader();
        reader.Documents[legacyActorId] = new ChatConversationCurrentStateDocument
        {
            Id = legacyActorId,
            ActorId = legacyActorId,
            ScopeId = "tenant",
            ConversationId = "admin-c1",
            Deleted = false,
        };
        var dispatch = new RecordingDispatchService();
        var store = new ActorBackedChatHistoryStore(
            new RecordingBootstrap(new StubActor(legacyActorId)),
            new StudioActorCommandDispatch(dispatch),
            reader,
            new RecordingDeliveryDocumentReader());

        var result = await store.DeleteConversationAsync("tenant-admin", "c1");

        result.Status.Should().Be(ChatHistoryDeleteResultStatus.NotFound);
        dispatch.Payloads.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteConversationAsync_ShouldDispatchTypedDeleteCommandToResolvedActor()
    {
        var actorId = ChatHistoryActorIds.Conversation("scope-a", "conversation-a");
        var reader = new RecordingDocumentReader();
        reader.Documents[actorId] = new ChatConversationCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
        };
        var bootstrap = new RecordingBootstrap(new StubActor(actorId));
        var dispatch = new RecordingDispatchService();
        var store = new ActorBackedChatHistoryStore(
            bootstrap,
            new StudioActorCommandDispatch(dispatch),
            reader,
            new RecordingDeliveryDocumentReader());

        var result = await store.DeleteConversationAsync("scope-a", "conversation-a");

        result.Status.Should().Be(ChatHistoryDeleteResultStatus.Accepted);
        bootstrap.ActorIds.Should().ContainSingle(actorId);
        var command = dispatch.Payloads.Should().ContainSingle().Which.Should()
            .BeOfType<DeleteConversationCommand>().Subject;
        command.ScopeId.Should().Be("scope-a");
        command.ConversationId.Should().Be("conversation-a");
    }

    [Fact]
    public async Task GetIndexAsync_ShouldPagePastTwoHundredFiftyConversationsWithStableOrdering()
    {
        var reader = new InMemoryProjectionDocumentStore<ChatConversationCurrentStateDocument, string>(
            document => document.ActorId,
            keyFormatter: key => key,
            defaultSortSelector: document => document.UpdatedAt,
            queryTakeMax: 300);
        for (var index = 0; index < 251; index++)
        {
            var conversationId = $"conversation-{index:000}";
            var actorId = ChatHistoryActorIds.Conversation("scope-a", conversationId);
            await reader.UpsertAsync(new ChatConversationCurrentStateDocument
            {
                Id = actorId,
                ActorId = actorId,
                ScopeId = "scope-a",
                ConversationId = conversationId,
                Title = conversationId,
                UpdatedAtMs = 251 - index,
                CreatedAtMs = 251 - index,
                Deleted = false,
            });
        }

        var store = new ActorBackedChatHistoryStore(
            new RecordingBootstrap(new StubActor("unused")),
            new StudioActorCommandDispatch(new RecordingDispatchService()),
            reader,
            new RecordingDeliveryDocumentReader());

        var firstPage = await store.GetIndexAsync(new ChatHistoryIndexPageRequest("scope-a", PageSize: 200));
        var secondPage = await store.GetIndexAsync(new ChatHistoryIndexPageRequest(
            "scope-a",
            PageSize: 200,
            Cursor: firstPage.NextCursor));

        firstPage.Conversations.Should().HaveCount(200);
        firstPage.NextCursor.Should().NotBeNullOrWhiteSpace();
        secondPage.Conversations.Should().HaveCount(51);
        secondPage.NextCursor.Should().BeNull();
        firstPage.Conversations.Select(static item => item.Id)
            .Concat(secondPage.Conversations.Select(static item => item.Id))
            .Should()
            .OnlyHaveUniqueItems()
            .And
            .HaveCount(251);
        secondPage.Conversations.Last().Id.Should().Be("conversation-250");
    }

    [Fact]
    public async Task GetCreateRecoveryAsync_ShouldResolveScopeBoundDeliveryReadModel()
    {
        var recoveryId = ChatHistoryCreateRecoveryIds.FromScopeAndCommandId("scope-a", "create-command-1");
        var deliveryReader = new RecordingDeliveryDocumentReader();
        deliveryReader.Documents[recoveryId] = new ChatHistoryCreateRecoveryCurrentStateDocument
        {
            Id = recoveryId,
            ActorId = "chat-history-delivery:actor",
            StateVersion = 3,
            ScopeId = "scope-a",
            ConversationId = "conversation-stable",
            TurnId = "turn-stable",
            WorkflowActorId = "run-1",
            WorkflowCommandId = "create-command-1",
            WorkflowCorrelationId = "corr-1",
            RequestFingerprint = "fingerprint-1",
            Status = "append_committed",
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-21T01:00:00Z")),
        };
        var store = new ActorBackedChatHistoryStore(
            new RecordingBootstrap(new StubActor("unused")),
            new StudioActorCommandDispatch(new RecordingDispatchService()),
            new RecordingDocumentReader(),
            deliveryReader);

        var result = await store.GetCreateRecoveryAsync("scope-a", "create-command-1");

        result.Status.Should().Be(ChatHistoryCreateRecoveryStatus.AppendCommitted);
        result.ScopeId.Should().Be("scope-a");
        result.CommandId.Should().Be("create-command-1");
        result.ConversationId.Should().Be("conversation-stable");
        result.TurnId.Should().Be("turn-stable");
        result.StateVersion.Should().Be(3);
    }

    private sealed class RecordingDispatchService
        : ICommandDispatchService<StudioActorCommand, StudioActorCommandReceipt, StudioActorCommandStartError>
    {
        public List<IMessage> Payloads { get; } = [];
        public List<StudioActorCommand> Commands { get; } = [];

        public Task<CommandDispatchResult<StudioActorCommandReceipt, StudioActorCommandStartError>> DispatchAsync(
            StudioActorCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            Payloads.Add(command.Payload);
            return Task.FromResult(
                CommandDispatchResult<StudioActorCommandReceipt, StudioActorCommandStartError>.Success(
                    new StudioActorCommandReceipt(command.Actor.Id, "command-1", "correlation-1")));
        }
    }

    private sealed class RecordingDocumentReader
        : IProjectionDocumentReader<ChatConversationCurrentStateDocument, string>
    {
        public Dictionary<string, ChatConversationCurrentStateDocument> Documents { get; } = new(StringComparer.Ordinal);
        public List<string> GetKeys { get; } = [];

        public Task<ChatConversationCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            GetKeys.Add(key);
            return Task.FromResult(Documents.GetValueOrDefault(key));
        }

        public Task<ProjectionDocumentQueryResult<ChatConversationCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionDocumentQueryResult<ChatConversationCurrentStateDocument>.Empty);
    }

    private sealed class RecordingDeliveryDocumentReader
        : IProjectionDocumentReader<ChatHistoryCreateRecoveryCurrentStateDocument, string>
    {
        public Dictionary<string, ChatHistoryCreateRecoveryCurrentStateDocument> Documents { get; } = new(StringComparer.Ordinal);

        public Task<ChatHistoryCreateRecoveryCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Documents.GetValueOrDefault(key));
        }

        public Task<ProjectionDocumentQueryResult<ChatHistoryCreateRecoveryCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionDocumentQueryResult<ChatHistoryCreateRecoveryCurrentStateDocument>.Empty);
    }

    private sealed class RecordingBootstrap(IActor actor) : IStudioActorBootstrap
    {
        public List<string> ActorIds { get; } = [];

        public Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor =>
            RecordActorAsync(actorId, ct);

        private Task<IActor> RecordActorAsync(string actorId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ActorIds.Add(actorId);
            return Task.FromResult(actor);
        }
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
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
    }
}
