using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Core.Prompting;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class ConversationContextAttachmentTests
{
    // Fix (review round 1, F1):
    //   Revision selection and degraded prompt-layer outcomes had no acceptance coverage.
    //   These tests assert prompt content, exact revision reads, placeholders, and diagnostics.
    [Fact]
    public void AttachmentSet_NormalizationRejectsDuplicatesAndRequiresPinnedRevision()
    {
        var duplicate = AttachmentSet(
            Attachment("artifact-a", ConversationContextAttachmentRevisionMode.FollowCurrent),
            Attachment("artifact-a", ConversationContextAttachmentRevisionMode.FollowCurrent));

        ConversationContextAttachmentAdmission.TryNormalize(duplicate, out _).Should().BeFalse();

        var missingRevision = AttachmentSet(
            Attachment("artifact-a", ConversationContextAttachmentRevisionMode.PinnedRevision));

        ConversationContextAttachmentAdmission.TryNormalize(missingRevision, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Materializer_FollowCurrentAdvancesWhilePinnedRevisionRemainsStableInSystemPrompt()
    {
        var query = new FakeContentArtifactQueryPort(BuildArtifact("rev-1"));
        var materializer = new ContentArtifactConversationPromptLayerMaterializer(query);
        var requester = new ContentArtifactPrincipalContract("principal-alpha", "user");
        var followCurrent = AttachmentSet(
            Attachment("artifact-a", ConversationContextAttachmentRevisionMode.FollowCurrent));
        var pinned = AttachmentSet(
            Attachment("artifact-a", ConversationContextAttachmentRevisionMode.PinnedRevision, "rev-1"));

        var followFirst = await materializer.MaterializeAsync("scope-alpha", requester, followCurrent);
        var pinnedFirst = await materializer.MaterializeAsync("scope-alpha", requester, pinned);

        query.Artifact = BuildArtifact("rev-2");
        var followSecond = await materializer.MaterializeAsync("scope-alpha", requester, followCurrent);
        var pinnedSecond = await materializer.MaterializeAsync("scope-alpha", requester, pinned);

        followFirst.Content.Should().Contain("revision_id=rev-1").And.Contain("content-rev-1");
        pinnedFirst.Content.Should().Contain("revision_id=rev-1").And.Contain("content-rev-1");
        followSecond.Content.Should().Contain("revision_id=rev-2")
            .And.Contain("revision_number=2")
            .And.Contain("updated_at_utc=2026-08-25T00:01:00.0000000+00:00")
            .And.Contain("content-rev-2");
        pinnedSecond.Content.Should().Contain("revision_id=rev-1").And.Contain("content-rev-1");
        query.ContentRevisionIds.Should().Equal("rev-1", "rev-1", "rev-2", "rev-1");

        var composed = SystemPromptLayerComposer.Compose(
            new KernelPromptLayer("kernel", new KernelPromptProvenance("kernel")),
            new BuiltInPromptFloorLayer("floor", new BuiltInPromptFloorProvenance("floor")),
            global: null,
            profile: null,
            selectedSkill: null,
            runtimeFacts: null,
            conversation: followSecond);
        composed.Prompt.Should().Contain("<untrusted-conversation-summary>")
            .And.Contain("artifact_id=artifact-a")
            .And.Contain("revision_id=rev-2")
            .And.Contain("content-rev-2");
        composed.Conversation.Included.Should().BeTrue();
    }

    [Theory]
    [InlineData("redacted", ConversationContextAttachmentUnavailableReason.Redacted)]
    [InlineData("tombstoned", ConversationContextAttachmentUnavailableReason.Tombstoned)]
    [InlineData("over-budget", ConversationContextAttachmentUnavailableReason.OverBudget)]
    [InlineData("read-model-unavailable", ConversationContextAttachmentUnavailableReason.ReadModelUnavailable)]
    [InlineData("backing-io", ConversationContextAttachmentUnavailableReason.BackingUnavailable)]
    [InlineData("typed-backing", ConversationContextAttachmentUnavailableReason.BackingUnavailable)]
    public async Task Materializer_DegradationAlwaysPairsPlaceholderAndDiagnostic(
        string failure,
        ConversationContextAttachmentUnavailableReason expectedReason)
    {
        var query = BuildFailingQuery(failure);
        var set = AttachmentSet(
            Attachment("artifact-a", ConversationContextAttachmentRevisionMode.FollowCurrent));

        var layer = await new ContentArtifactConversationPromptLayerMaterializer(query)
            .MaterializeAsync(
                "scope-alpha",
                new ContentArtifactPrincipalContract("principal-alpha", "user"),
                set);

        layer.Content.Should().Contain("[content-artifact-unavailable artifact_id=artifact-a")
            .And.Contain($"reason={expectedReason}")
            .And.NotContain("[/content-artifact]");
        layer.Diagnostics.Should().ContainSingle();
        layer.Diagnostics[0].Detail.Should().Contain("artifact-a").And.Contain(expectedReason.ToString());
    }

    [Fact]
    public async Task MaterializeOrDegradeAsync_WithoutReadPortEmitsOnePairPerSealedAttachment()
    {
        var set = AttachmentSet(
            Attachment("artifact-a", ConversationContextAttachmentRevisionMode.FollowCurrent),
            Attachment("artifact-b", ConversationContextAttachmentRevisionMode.PinnedRevision, "rev-7"));

        var layer = await ContentArtifactConversationPromptLayerMaterializer.MaterializeOrDegradeAsync(
            materializer: null,
            set,
            "scope-alpha",
            "principal-alpha");

        layer.Should().NotBeNull();
        layer!.Content.Should().Contain("artifact_id=artifact-a")
            .And.Contain("artifact_id=artifact-b")
            .And.Contain("reason=ReadModelUnavailable");
        layer.Diagnostics.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(null, "principal-alpha")]
    [InlineData("scope-alpha", null)]
    public async Task MaterializeOrDegradeAsync_WithoutTypedCallerAuthorityDoesNotInventScope(
        string? scopeId,
        string? principalId)
    {
        var query = new FakeContentArtifactQueryPort(BuildArtifact("rev-1"));
        var set = AttachmentSet(
            Attachment("artifact-a", ConversationContextAttachmentRevisionMode.FollowCurrent));

        var layer = await ContentArtifactConversationPromptLayerMaterializer.MaterializeOrDegradeAsync(
            new ContentArtifactConversationPromptLayerMaterializer(query),
            set,
            scopeId,
            principalId);

        layer.Should().NotBeNull();
        layer!.Content.Should().Contain("reason=AccessDenied");
        layer.Diagnostics.Should().ContainSingle();
        query.GetCalls.Should().Be(0);
    }

    private static FakeContentArtifactQueryPort BuildFailingQuery(string failure)
    {
        var lifecycle = failure == "tombstoned"
            ? ContentArtifactLifecycleStatusNames.Tombstoned
            : ContentArtifactLifecycleStatusNames.Active;
        var availability = failure == "redacted"
            ? ContentArtifactRevisionAvailabilityNames.Redacted
            : ContentArtifactRevisionAvailabilityNames.Available;
        var query = new FakeContentArtifactQueryPort(
            BuildArtifact("rev-2", lifecycle, availability));

        if (failure == "over-budget")
            query.ContentByRevision["rev-2"] = new byte[ContentArtifactConversationPromptLayerMaterializer.MaximumAttachmentBytes + 1];
        else if (failure == "read-model-unavailable")
            query.GetException = new InvalidOperationException("read model unavailable");
        else if (failure == "backing-io")
            query.ContentException = new IOException("backing provider unavailable");
        else if (failure == "typed-backing")
        {
            query.ContentException = new ContentArtifactContentUnavailableException(
                "artifact-a",
                "rev-2",
                ContentArtifactContentUnavailableReason.BackingUnavailable);
        }

        return query;
    }

    private static ContentArtifactCurrentStateResponse BuildArtifact(
        string currentRevisionId,
        string lifecycle = ContentArtifactLifecycleStatusNames.Active,
        string currentAvailability = ContentArtifactRevisionAvailabilityNames.Available) =>
        new(
            "artifact-a",
            "scope-alpha",
            null,
            "text",
            "Report",
            "plain",
            lifecycle,
            currentRevisionId,
            2,
            2,
            new ContentArtifactPrincipalContract("principal-alpha", "user"),
            [],
            [],
            null,
            null,
            [
                Revision("rev-1", 1, ContentArtifactRevisionAvailabilityNames.Available),
                Revision("rev-2", 2, currentAvailability),
            ],
            DateTimeOffset.Parse("2026-08-25T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-25T00:01:00Z"));

    private static ContentArtifactRevisionResponse Revision(
        string revisionId,
        long revisionNumber,
        string availability) =>
        new(
            revisionId,
            revisionNumber,
            revisionNumber == 1 ? null : "rev-1",
            "text/plain",
            13,
            $"hash-{revisionId}",
            availability,
            true,
            false,
            new ContentArtifactExecutionProvenanceContract("scope-alpha"),
            [],
            DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

    private static ConversationContextAttachmentSet AttachmentSet(
        params ConversationContextAttachment[] attachments)
    {
        var set = new ConversationContextAttachmentSet();
        set.Attachments.Add(attachments);
        return set;
    }

    private static ConversationContextAttachment Attachment(
        string artifactId,
        ConversationContextAttachmentRevisionMode revisionMode,
        string pinnedRevisionId = "") =>
        new()
        {
            ArtifactId = artifactId,
            RevisionMode = revisionMode,
            PinnedRevisionId = pinnedRevisionId,
        };

    private sealed class FakeContentArtifactQueryPort(
        ContentArtifactCurrentStateResponse artifact) : IContentArtifactQueryPort
    {
        public ContentArtifactCurrentStateResponse Artifact { get; set; } = artifact;
        public Exception? GetException { get; set; }
        public Exception? ContentException { get; set; }
        public int GetCalls { get; private set; }
        public List<string> ContentRevisionIds { get; } = [];
        public Dictionary<string, byte[]> ContentByRevision { get; } = new(StringComparer.Ordinal)
        {
            ["rev-1"] = Encoding.UTF8.GetBytes("content-rev-1"),
            ["rev-2"] = Encoding.UTF8.GetBytes("content-rev-2"),
        };

        public Task<ContentArtifactListResponse> ListAsync(
            string scopeId,
            string requesterPrincipalId,
            ContentArtifactQueryRequest query,
            CancellationToken ct = default) =>
            Task.FromResult(new ContentArtifactListResponse(scopeId, [Artifact]));

        public Task<ContentArtifactCurrentStateResponse?> GetAsync(
            string scopeId,
            string artifactId,
            CancellationToken ct = default)
        {
            GetCalls++;
            return GetException is null
                ? Task.FromResult<ContentArtifactCurrentStateResponse?>(
                    Artifact.ArtifactId == artifactId ? Artifact : null)
                : Task.FromException<ContentArtifactCurrentStateResponse?>(GetException);
        }

        public Task<ContentArtifactCurrentStateResponse?> GetByDedupKeyAsync(
            string scopeId,
            string dedupKey,
            CancellationToken ct = default) =>
            Task.FromResult<ContentArtifactCurrentStateResponse?>(null);

        public Task<ContentArtifactRevisionContentResponse> GetRevisionContentAsync(
            string scopeId,
            string artifactId,
            string revisionId,
            ContentArtifactPrincipalContract requester,
            CancellationToken ct = default)
        {
            ContentRevisionIds.Add(revisionId);
            if (ContentException is not null)
                return Task.FromException<ContentArtifactRevisionContentResponse>(ContentException);

            var content = ContentByRevision[revisionId];
            return Task.FromResult(new ContentArtifactRevisionContentResponse(
                new ContentArtifactReferenceContract(
                    artifactId,
                    revisionId,
                    $"hash-{revisionId}",
                    "text/plain"),
                content));
        }
    }
}
