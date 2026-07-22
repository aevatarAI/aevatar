using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Infrastructure.ActorBacked;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class ProjectionChatConversationContinuationAdmissionReaderTests
{
    [Fact]
    public async Task GetContinuationAsync_ShouldReadConversationCurrentStateByActorId()
    {
        var actorId = ChatHistoryActorIds.Conversation("scope-alpha", "conversation-alpha");
        var documentReader = new RecordingConversationDocumentReader();
        documentReader.Seed(new ChatConversationCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            ScopeId = "scope-alpha",
            ConversationId = "conversation-alpha",
            Deleted = false,
            StateVersion = 5,
            Turns =
            {
                new ChatConversationTurnDocument
                {
                    TurnId = "turn-1",
                    Sequence = 1,
                    UserText = "Create a workflow that generates fund analysis reports.",
                    AssistantText = "Choose a Team: team01 or team02.",
                },
            },
        });
        var admissionReader = new ProjectionChatConversationContinuationAdmissionReader(documentReader);

        var admission = await admissionReader.GetContinuationAsync(
            " scope-alpha ",
            " conversation-alpha ",
            minimumStateVersion: 5);

        admission.CanContinue.Should().BeTrue();
        admission.ConversationContext.Should().NotBeNull();
        admission.ConversationContext!.ScopeId.Should().Be("scope-alpha");
        admission.ConversationContext.ConversationId.Should().Be("conversation-alpha");
        admission.ConversationContext.StateVersion.Should().Be(5);
        admission.ConversationContext.Messages
            .Select(static message => (message.Sequence, message.TurnId, message.Role, message.Content))
            .Should()
            .Equal(
                (1, "turn-1", WorkflowConversationExecutionRole.User, "Create a workflow that generates fund analysis reports."),
                (2, "turn-1", WorkflowConversationExecutionRole.Assistant, "Choose a Team: team01 or team02."));
        documentReader.GetKeys.Should().ContainSingle()
            .Which.Should().Be(actorId);
    }

    [Fact]
    public async Task GetContinuationAsync_ShouldReturnNotFound_WhenConversationDocumentIsMissing()
    {
        var documentReader = new RecordingConversationDocumentReader();
        var admissionReader = new ProjectionChatConversationContinuationAdmissionReader(documentReader);

        var admission = await admissionReader.GetContinuationAsync(
            "scope-alpha",
            "conversation-missing",
            minimumStateVersion: 1);

        admission.CanContinue.Should().BeFalse();
        admission.ConversationContext.Should().BeNull();
        documentReader.GetKeys.Should().Equal(
            ChatHistoryActorIds.Conversation("scope-alpha", "conversation-missing"),
            ChatHistoryActorIds.LegacyConversation("scope-alpha", "conversation-missing"));
    }

    [Fact]
    public async Task GetContinuationAsync_ShouldReturnNotReady_WhenReadModelIsBelowMinimumStateVersion()
    {
        var actorId = ChatHistoryActorIds.Conversation("scope-alpha", "conversation-alpha");
        var documentReader = new RecordingConversationDocumentReader();
        documentReader.Seed(new ChatConversationCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            ScopeId = "scope-alpha",
            ConversationId = "conversation-alpha",
            Deleted = false,
            StateVersion = 4,
            Turns =
            {
                new ChatConversationTurnDocument
                {
                    TurnId = "turn-1",
                    Sequence = 1,
                    UserText = "Create a workflow that generates fund analysis reports.",
                    AssistantText = "Choose a Team: team01 or team02.",
                },
            },
        });
        var admissionReader = new ProjectionChatConversationContinuationAdmissionReader(documentReader);

        var admission = await admissionReader.GetContinuationAsync(
            "scope-alpha",
            "conversation-alpha",
            minimumStateVersion: 5);

        admission.CanContinue.Should().BeFalse();
        admission.Failure.Should().Be(ChatConversationContinuationAdmissionFailure.ReadModelNotReady);
        admission.ConversationContext.Should().BeNull();
    }

    [Fact]
    public async Task GetContinuationAsync_ShouldReturnNotReady_WhenMinimumStateVersionIsNotPositive()
    {
        var actorId = ChatHistoryActorIds.Conversation("scope-alpha", "conversation-alpha");
        var documentReader = new RecordingConversationDocumentReader();
        documentReader.Seed(new ChatConversationCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            ScopeId = "scope-alpha",
            ConversationId = "conversation-alpha",
            Deleted = false,
            StateVersion = 5,
            Turns =
            {
                new ChatConversationTurnDocument
                {
                    TurnId = "turn-1",
                    Sequence = 1,
                    UserText = "Create a workflow that generates fund analysis reports.",
                    AssistantText = "Choose a Team: team01 or team02.",
                },
            },
        });
        var admissionReader = new ProjectionChatConversationContinuationAdmissionReader(documentReader);

        var admission = await admissionReader.GetContinuationAsync(
            "scope-alpha",
            "conversation-alpha",
            minimumStateVersion: 0);

        admission.CanContinue.Should().BeFalse();
        admission.Failure.Should().Be(ChatConversationContinuationAdmissionFailure.ReadModelNotReady);
        admission.ConversationContext.Should().BeNull();
    }

    [Fact]
    public async Task GetContinuationAsync_ShouldReturnNotReady_WhenReadModelHasNoUsableContextMessages()
    {
        var actorId = ChatHistoryActorIds.Conversation("scope-alpha", "conversation-alpha");
        var documentReader = new RecordingConversationDocumentReader();
        documentReader.Seed(new ChatConversationCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            ScopeId = "scope-alpha",
            ConversationId = "conversation-alpha",
            Deleted = false,
            StateVersion = 5,
            Turns =
            {
                new ChatConversationTurnDocument
                {
                    TurnId = "turn-1",
                    Sequence = 1,
                    UserText = " ",
                    AssistantText = "",
                },
            },
        });
        var admissionReader = new ProjectionChatConversationContinuationAdmissionReader(documentReader);

        var admission = await admissionReader.GetContinuationAsync(
            "scope-alpha",
            "conversation-alpha",
            minimumStateVersion: 1);

        admission.CanContinue.Should().BeFalse();
        admission.Failure.Should().Be(ChatConversationContinuationAdmissionFailure.ReadModelNotReady);
        admission.ConversationContext.Should().BeNull();
    }

    [Fact]
    public async Task GetContinuationAsync_ShouldReturnNotFound_WhenConversationDocumentIsDeleted()
    {
        var actorId = ChatHistoryActorIds.Conversation("scope-alpha", "conversation-deleted");
        var documentReader = new RecordingConversationDocumentReader();
        documentReader.Seed(new ChatConversationCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            ScopeId = "scope-alpha",
            ConversationId = "conversation-deleted",
            Deleted = true,
        });
        var admissionReader = new ProjectionChatConversationContinuationAdmissionReader(documentReader);

        var admission = await admissionReader.GetContinuationAsync(
            "scope-alpha",
            "conversation-deleted",
            minimumStateVersion: 1);

        admission.CanContinue.Should().BeFalse();
        admission.ConversationContext.Should().BeNull();
    }

    [Theory]
    [InlineData("scope-other", "conversation-alpha")]
    [InlineData("scope-alpha", "conversation-other")]
    public async Task GetContinuationAsync_ShouldReturnNotFound_WhenDocumentIdentityDoesNotMatchRequest(
        string documentScopeId,
        string documentConversationId)
    {
        var actorId = ChatHistoryActorIds.Conversation("scope-alpha", "conversation-alpha");
        var documentReader = new RecordingConversationDocumentReader();
        documentReader.Seed(new ChatConversationCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            ScopeId = documentScopeId,
            ConversationId = documentConversationId,
            Deleted = false,
        });
        var admissionReader = new ProjectionChatConversationContinuationAdmissionReader(documentReader);

        var admission = await admissionReader.GetContinuationAsync(
            "scope-alpha",
            "conversation-alpha",
            minimumStateVersion: 1);

        admission.CanContinue.Should().BeFalse();
        admission.ConversationContext.Should().BeNull();
    }

    [Fact]
    public async Task GetContinuationAsync_ShouldTrimOldestMessagesDeterministically()
    {
        var actorId = ChatHistoryActorIds.Conversation("scope-alpha", "conversation-alpha");
        var documentReader = new RecordingConversationDocumentReader();
        var document = new ChatConversationCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            ScopeId = "scope-alpha",
            ConversationId = "conversation-alpha",
            Deleted = false,
            StateVersion = 15,
        };
        for (var i = 1; i <= 13; i++)
        {
            document.Turns.Add(new ChatConversationTurnDocument
            {
                TurnId = $"turn-{i}",
                Sequence = i,
                UserText = $"user-{i}",
                AssistantText = $"assistant-{i}",
            });
        }
        documentReader.Seed(document);
        var admissionReader = new ProjectionChatConversationContinuationAdmissionReader(documentReader);

        var admission = await admissionReader.GetContinuationAsync(
            "scope-alpha",
            "conversation-alpha",
            minimumStateVersion: 15);

        admission.CanContinue.Should().BeTrue();
        admission.ConversationContext.Should().NotBeNull();
        admission.ConversationContext!.Truncated.Should().BeTrue();
        admission.ConversationContext.MaxMessageCount.Should().Be(24);
        admission.ConversationContext.Messages.Should().HaveCount(24);
        admission.ConversationContext.Messages.First().Content.Should().Be("user-2");
        admission.ConversationContext.Messages.Last().Content.Should().Be("assistant-13");
        admission.ConversationContext.Messages.Select(static message => message.Sequence)
            .Should()
            .Equal(Enumerable.Range(1, 24));
    }

    private sealed class RecordingConversationDocumentReader
        : IProjectionDocumentReader<ChatConversationCurrentStateDocument, string>
    {
        private readonly Dictionary<string, ChatConversationCurrentStateDocument> _documents = new(StringComparer.Ordinal);

        public List<string> GetKeys { get; } = [];

        public void Seed(ChatConversationCurrentStateDocument document) =>
            _documents[document.Id] = document;

        public Task<ChatConversationCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            GetKeys.Add(key);
            return Task.FromResult(_documents.GetValueOrDefault(key));
        }

        public Task<ProjectionDocumentQueryResult<ChatConversationCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionDocumentQueryResult<ChatConversationCurrentStateDocument>.Empty);
    }
}
