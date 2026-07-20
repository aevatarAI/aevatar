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
            reader);
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
        public ChatConversationCurrentStateDocument? Document { get; init; }

        public Task<ChatConversationCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default) =>
            Task.FromResult(Document);

        public Task<ProjectionDocumentQueryResult<ChatConversationCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionDocumentQueryResult<ChatConversationCurrentStateDocument>.Empty);
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
