using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class ConversationContextAttachmentTests
{
    [Fact]
    public void AttachmentSet_NormalizationRejectsDuplicatesAndRequiresPinnedRevision()
    {
        var duplicate = new ConversationContextAttachmentSet();
        duplicate.Attachments.Add(new ConversationContextAttachment
        {
            ArtifactId = "artifact-a",
            RevisionMode = ConversationContextAttachmentRevisionMode.FollowCurrent,
        });
        duplicate.Attachments.Add(duplicate.Attachments[0].Clone());

        ConversationContextAttachmentAdmission.TryNormalize(duplicate, out _).Should().BeFalse();

        var missingRevision = new ConversationContextAttachmentSet();
        missingRevision.Attachments.Add(new ConversationContextAttachment
        {
            ArtifactId = "artifact-a",
            RevisionMode = ConversationContextAttachmentRevisionMode.PinnedRevision,
        });
        ConversationContextAttachmentAdmission.TryNormalize(missingRevision, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Materializer_UsesCurrentRevisionAndEmitsIdentityHeader()
    {
        var query = new FakeContentArtifactQueryPort(
            new ContentArtifactCurrentStateResponse(
                "artifact-a", "scope-a", null, "text", "Report", "plain", "active", "rev-2", 2, 2,
                new ContentArtifactPrincipalContract("user-a", "user"), [], [], null, null,
                [
                    new ContentArtifactRevisionResponse("rev-1", 1, null, "text/plain", 5, "hash-one", "available", true, false, new ContentArtifactExecutionProvenanceContract("scope-a"), [], DateTimeOffset.UtcNow),
                    new ContentArtifactRevisionResponse("rev-2", 2, "rev-1", "text/plain", 5, "hash-two", "available", true, false, new ContentArtifactExecutionProvenanceContract("scope-a"), [], DateTimeOffset.UtcNow),
                ], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new byte[] { 1, 2, 3 });
        var set = new ConversationContextAttachmentSet();
        set.Attachments.Add(new ConversationContextAttachment
        {
            ArtifactId = "artifact-a",
            RevisionMode = ConversationContextAttachmentRevisionMode.FollowCurrent,
        });

        var layer = await new ContentArtifactConversationPromptLayerMaterializer(query)
            .MaterializeAsync("scope-a", new ContentArtifactPrincipalContract("user-a", "user"), set);

        layer.Content.Should().Contain("artifact_id=artifact-a");
        layer.Content.Should().Contain("revision_id=rev-2");
        layer.Content.Should().Contain("content_hash=hash-two");
        layer.Content.Should().Contain("\u0001\u0002\u0003");
        layer.Diagnostics.Should().BeEmpty();
    }

    private sealed class FakeContentArtifactQueryPort(
        ContentArtifactCurrentStateResponse artifact,
        byte[] content) : IContentArtifactQueryPort
    {
        public Task<ContentArtifactListResponse> ListAsync(string scopeId, string requesterPrincipalId, ContentArtifactQueryRequest query, CancellationToken ct = default) =>
            Task.FromResult(new ContentArtifactListResponse(scopeId, [artifact]));

        public Task<ContentArtifactCurrentStateResponse?> GetAsync(string scopeId, string artifactId, CancellationToken ct = default) =>
            Task.FromResult<ContentArtifactCurrentStateResponse?>(artifact.ArtifactId == artifactId ? artifact : null);

        public Task<ContentArtifactCurrentStateResponse?> GetByDedupKeyAsync(string scopeId, string dedupKey, CancellationToken ct = default) =>
            Task.FromResult<ContentArtifactCurrentStateResponse?>(null);

        public Task<ContentArtifactRevisionContentResponse> GetRevisionContentAsync(string scopeId, string artifactId, string revisionId, ContentArtifactPrincipalContract requester, CancellationToken ct = default) =>
            Task.FromResult(new ContentArtifactRevisionContentResponse(
                new ContentArtifactReferenceContract(artifactId, revisionId, "hash-two", "text/plain"), content));
    }
}
