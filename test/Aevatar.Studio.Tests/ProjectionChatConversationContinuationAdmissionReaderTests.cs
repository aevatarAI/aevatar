using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Infrastructure.ActorBacked;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class ProjectionChatConversationContinuationAdmissionReaderTests
{
    [Fact]
    public async Task CanContinueAsync_ShouldReadConversationCurrentStateByActorId()
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
        });
        var admissionReader = new ProjectionChatConversationContinuationAdmissionReader(documentReader);

        var canContinue = await admissionReader.CanContinueAsync(
            " scope-alpha ",
            " conversation-alpha ");

        canContinue.Should().BeTrue();
        documentReader.GetKeys.Should().ContainSingle()
            .Which.Should().Be(actorId);
    }

    [Fact]
    public async Task CanContinueAsync_ShouldReturnFalse_WhenConversationDocumentIsMissing()
    {
        var documentReader = new RecordingConversationDocumentReader();
        var admissionReader = new ProjectionChatConversationContinuationAdmissionReader(documentReader);

        var canContinue = await admissionReader.CanContinueAsync(
            "scope-alpha",
            "conversation-missing");

        canContinue.Should().BeFalse();
        documentReader.GetKeys.Should().ContainSingle()
            .Which.Should().Be(ChatHistoryActorIds.Conversation("scope-alpha", "conversation-missing"));
    }

    [Fact]
    public async Task CanContinueAsync_ShouldReturnFalse_WhenConversationDocumentIsDeleted()
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

        var canContinue = await admissionReader.CanContinueAsync(
            "scope-alpha",
            "conversation-deleted");

        canContinue.Should().BeFalse();
    }

    [Theory]
    [InlineData("scope-other", "conversation-alpha")]
    [InlineData("scope-alpha", "conversation-other")]
    public async Task CanContinueAsync_ShouldReturnFalse_WhenDocumentIdentityDoesNotMatchRequest(
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

        var canContinue = await admissionReader.CanContinueAsync(
            "scope-alpha",
            "conversation-alpha");

        canContinue.Should().BeFalse();
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
