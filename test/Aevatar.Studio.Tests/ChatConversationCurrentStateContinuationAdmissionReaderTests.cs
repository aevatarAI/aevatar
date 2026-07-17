using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Infrastructure.ActorBacked;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class ChatConversationCurrentStateContinuationAdmissionReaderTests
{
    [Fact]
    public async Task CanContinueAsync_ShouldReturnTrue_WhenCurrentStateDocumentMatchesConversation()
    {
        var document = NewDocument("scope-alpha", "conversation-alpha");
        var documentReader = new StubConversationDocumentReader(document);
        var admissionReader = new ChatConversationCurrentStateContinuationAdmissionReader(documentReader);

        var canContinue = await admissionReader.CanContinueAsync("scope-alpha", "conversation-alpha");

        canContinue.Should().BeTrue();
        documentReader.GetKeys.Should().ContainSingle()
            .Which.Should().Be(ChatHistoryActorIds.Conversation("scope-alpha", "conversation-alpha"));
    }

    [Fact]
    public async Task CanContinueAsync_ShouldReturnFalse_WhenCurrentStateDocumentIsNotContinuable()
    {
        var missingReader = new StubConversationDocumentReader();
        var missingAdmissionReader = new ChatConversationCurrentStateContinuationAdmissionReader(missingReader);
        var deletedAdmissionReader = new ChatConversationCurrentStateContinuationAdmissionReader(
            new StubConversationDocumentReader(NewDocument("scope-alpha", "conversation-alpha", deleted: true)));
        var wrongScopeAdmissionReader = new ChatConversationCurrentStateContinuationAdmissionReader(
            new StubConversationDocumentReader(NewDocument("scope-beta", "conversation-alpha")));
        var wrongConversationAdmissionReader = new ChatConversationCurrentStateContinuationAdmissionReader(
            new StubConversationDocumentReader(NewDocument("scope-alpha", "conversation-beta")));
        var wrongActorAdmissionReader = new ChatConversationCurrentStateContinuationAdmissionReader(
            new StubConversationDocumentReader(NewDocument(
                "scope-alpha",
                "conversation-alpha",
                actorId: ChatHistoryActorIds.Conversation("scope-alpha", "conversation-beta"))));

        (await missingAdmissionReader.CanContinueAsync("scope-alpha", "conversation-alpha")).Should().BeFalse();
        (await deletedAdmissionReader.CanContinueAsync("scope-alpha", "conversation-alpha")).Should().BeFalse();
        (await wrongScopeAdmissionReader.CanContinueAsync("scope-alpha", "conversation-alpha")).Should().BeFalse();
        (await wrongConversationAdmissionReader.CanContinueAsync("scope-alpha", "conversation-alpha")).Should().BeFalse();
        (await wrongActorAdmissionReader.CanContinueAsync("scope-alpha", "conversation-alpha")).Should().BeFalse();
    }

    private static ChatConversationCurrentStateDocument NewDocument(
        string scopeId,
        string conversationId,
        bool deleted = false,
        string? actorId = null)
    {
        var resolvedActorId = actorId ?? ChatHistoryActorIds.Conversation("scope-alpha", "conversation-alpha");
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        return new ChatConversationCurrentStateDocument
        {
            Id = resolvedActorId,
            ActorId = resolvedActorId,
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAt = now,
            ScopeId = scopeId,
            ConversationId = conversationId,
            Title = "Conversation Alpha",
            CreatedAtMs = now.ToDateTimeOffset().ToUnixTimeMilliseconds(),
            UpdatedAtMs = now.ToDateTimeOffset().ToUnixTimeMilliseconds(),
            Deleted = deleted,
        };
    }

    private sealed class StubConversationDocumentReader
        : IProjectionDocumentReader<ChatConversationCurrentStateDocument, string>
    {
        private readonly ChatConversationCurrentStateDocument? _document;

        public StubConversationDocumentReader(ChatConversationCurrentStateDocument? document = null)
        {
            _document = document;
        }

        public List<string> GetKeys { get; } = [];

        public Task<ChatConversationCurrentStateDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            GetKeys.Add(key);
            return Task.FromResult(_document);
        }

        public Task<ProjectionDocumentQueryResult<ChatConversationCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }
    }
}
