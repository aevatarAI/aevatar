using System.Security.Cryptography;
using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ContentArtifacts;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting;
using Aevatar.Studio.Projection.DependencyInjection;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests.ContentArtifacts;

public sealed class ContentArtifactProjectionTests
{
    private const string ScopeId = "scope-1";
    private const string ArtifactId = "artifact-1";
    private static readonly string ActorId = ContentArtifactConventions.BuildActorId(ScopeId, ArtifactId);

    [Fact]
    public void ReadModelProviders_ShouldRegisterContentArtifactStore()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddStudioProjectionComponents();
        services.AddStudioProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionDocumentReader<ContentArtifactCurrentStateDocument, string>>()
            .Should().BeOfType<InMemoryProjectionDocumentStore<ContentArtifactCurrentStateDocument, string>>();
        provider.GetRequiredService<IProjectionDocumentWriter<ContentArtifactCurrentStateDocument>>()
            .Should().BeOfType<InMemoryProjectionDocumentStore<ContentArtifactCurrentStateDocument, string>>();
    }

    [Fact]
    public async Task ProjectAsync_ShouldCopyAuthoritativeCurrentStateAndImmutableRevisionFacts()
    {
        var observedAt = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var updatedAt = observedAt.AddMinutes(-5);
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new ContentArtifactCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(observedAt));
        var state = BuildState(updatedAt);

        await projector.ProjectAsync(
            new StudioMaterializationContext
            {
                RootActorId = ActorId,
                ProjectionKind = ContentArtifactGAgent.ProjectionKind,
            },
            WrapCommitted(state, observedAt));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.Id.Should().Be(ActorId);
        document.StateVersion.Should().Be(7);
        document.ArtifactId.Should().Be(ArtifactId);
        document.ScopeId.Should().Be(ScopeId);
        document.TeamId.Should().Be("team-1");
        document.OwnerPrincipalId.Should().Be("owner-1");
        document.CurrentRevisionId.Should().Be("revision-2");
        document.ConcurrencyVersion.Should().Be(3);
        document.ArtifactUpdatedAtUtc.ToDateTimeOffset().Should().Be(updatedAt);
        document.Revisions.Select(static revision => revision.RevisionNumber).Should().Equal(1, 2);
        document.Revisions[0].InlineContent.ToStringUtf8().Should().Be("revision one");
        document.Revisions[1].ProvenanceRunId.Should().Be("run-1");
        document.Revisions[1].Citations.Should().ContainSingle()
            .Which.SourceArtifactRevisionId.Should().Be("source-revision-1");
    }

    [Fact]
    public async Task QueryPort_ShouldFilterByScopeReadablePrincipalAndResolveExactRevision()
    {
        var document = ContentArtifactCurrentStateProjector.ToDocument(
            ActorId,
            new StateEvent { Version = 7, EventId = "event-7" },
            BuildState(DateTimeOffset.Parse("2026-07-20T09:00:00Z")),
            DateTimeOffset.Parse("2026-07-20T10:00:00Z"));
        var reader = new RecordingDocumentReader(document);
        var queryPort = new ProjectionContentArtifactQueryPort(reader, backingContentPort: null);

        var list = await queryPort.ListAsync(
            ScopeId,
            "owner-1",
            new ContentArtifactQueryRequest(
                TeamId: "team-1",
                Kind: "markdown",
                RunId: "run-1"));
        var current = await queryPort.GetAsync(ScopeId, ArtifactId);

        list.Artifacts.Should().ContainSingle();
        current.Should().NotBeNull();
        current!.Revisions.Select(static revision => revision.RevisionId).Should()
            .Equal("revision-1", "revision-2");
        reader.LastQuery!.Filters.Should().Contain(filter => filter.FieldPath == "scope_id");
        reader.LastQuery.Filters.Should().NotContain(filter => filter.FieldPath == "owner_principal_id");
        reader.LastQuery.AnyOfFilters.Should().Contain(filter => filter.FieldPath == "owner_principal_id");
        reader.LastQuery.AnyOfFilters.Should().Contain(filter => filter.FieldPath == "reader_principal_ids");
        reader.LastQuery.Filters.Should().Contain(filter => filter.FieldPath == "team_id");
        reader.LastQuery.Filters.Should().Contain(filter => filter.FieldPath == "kind");
        reader.LastQuery.Filters.Should().Contain(filter => filter.FieldPath == "provenance_run_ids");
    }

    [Fact]
    public async Task GetByDedupKeyAsync_ShouldUseCanonicalArtifactAddress()
    {
        var document = ContentArtifactCurrentStateProjector.ToDocument(
            ActorId,
            new StateEvent { Version = 7, EventId = "event-7" },
            BuildState(DateTimeOffset.Parse("2026-07-20T09:00:00Z")),
            DateTimeOffset.Parse("2026-07-20T10:00:00Z"));
        var reader = new RecordingDocumentReader(document);
        var queryPort = new ProjectionContentArtifactQueryPort(reader);

        await queryPort.GetByDedupKeyAsync(ScopeId, "report-dedup");

        var artifactId = ContentArtifactConventions.BuildArtifactId(ScopeId, "report-dedup");
        reader.LastKey.Should().Be(ContentArtifactConventions.BuildActorId(ScopeId, artifactId));
    }

    [Fact]
    public async Task ListAsync_ShouldApplyReadableAclBeforeCursorPaging()
    {
        var store = new InMemoryProjectionDocumentStore<ContentArtifactCurrentStateDocument, string>(
            keySelector: document => document.Id);
        await store.UpsertAsync(BuildListDocument("owner-artifact", "caller-1"));
        await store.UpsertAsync(BuildListDocument(
            "reader-artifact",
            "other-owner",
            readers: ["caller-1"],
            lifecycleStatus: ContentArtifactLifecycleStatusNames.Tombstoned));
        await store.UpsertAsync(BuildListDocument(
            "writer-artifact",
            "other-owner",
            writers: ["caller-1"]));
        await store.UpsertAsync(BuildListDocument("unrelated-artifact", "other-owner"));
        var queryPort = new ProjectionContentArtifactQueryPort(store);

        var first = await queryPort.ListAsync(
            ScopeId,
            "caller-1",
            new ContentArtifactQueryRequest(PageSize: 1));
        var second = await queryPort.ListAsync(
            ScopeId,
            "caller-1",
            new ContentArtifactQueryRequest(PageSize: 1, PageToken: first.NextPageToken));

        first.NextPageToken.Should().NotBeNullOrWhiteSpace();
        second.NextPageToken.Should().BeNull();
        first.Artifacts.Concat(second.Artifacts).Select(artifact => artifact.ArtifactId).Should()
            .BeEquivalentTo(["owner-artifact", "reader-artifact"]);
    }

    [Fact]
    public async Task GetRevisionContentAsync_ShouldVerifyInlineHashAndRejectTamperedProjection()
    {
        var document = ContentArtifactCurrentStateProjector.ToDocument(
            ActorId,
            new StateEvent { Version = 7, EventId = "event-7" },
            BuildState(DateTimeOffset.Parse("2026-07-20T09:00:00Z")),
            DateTimeOffset.Parse("2026-07-20T10:00:00Z"));
        var queryPort = new ProjectionContentArtifactQueryPort(
            new RecordingDocumentReader(document),
            backingContentPort: null);

        var content = await queryPort.GetRevisionContentAsync(
            ScopeId,
            ArtifactId,
            "revision-1",
            Principal("owner-1"));

        content.Content.Should().Equal(ByteString.CopyFromUtf8("revision one").ToByteArray());
        content.Reference.ContentHash.Should().Be(ContentHash("revision one"));

        document.Revisions[0].ContentHash = new string('0', 64);
        var tampered = new ProjectionContentArtifactQueryPort(
            new RecordingDocumentReader(document),
            backingContentPort: null);
        var act = () => tampered.GetRevisionContentAsync(
            ScopeId,
            ArtifactId,
            "revision-1",
            Principal("owner-1"));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*content hash verification failed*");
    }

    [Fact]
    public async Task GetRevisionContentAsync_ShouldTreatMissingBackingContentAsAReadFailureNotGone()
    {
        var document = ContentArtifactCurrentStateProjector.ToDocument(
            ActorId,
            new StateEvent { Version = 7, EventId = "event-7" },
            BuildState(DateTimeOffset.Parse("2026-07-20T09:00:00Z")),
            DateTimeOffset.Parse("2026-07-20T10:00:00Z"));
        var backed = document.Revisions[1];
        backed.InlineContent = ByteString.Empty;
        backed.ContentLocationKind = "backing_object";
        backed.BackingProvider = "object-store";
        backed.BackingObjectKey = "missing-object";
        var queryPort = new ProjectionContentArtifactQueryPort(
            new RecordingDocumentReader(document),
            new MissingBackingContentPort());

        var act = () => queryPort.GetRevisionContentAsync(
            ScopeId,
            ArtifactId,
            "revision-2",
            Principal("owner-1"));

        var exception = (await act.Should().ThrowAsync<IOException>()).Which;
        exception.Message.Should().Contain("backing content is missing");
    }

    [Fact]
    public async Task GetRevisionContentAsync_ShouldAuthorizeAgainstTheContentReadSnapshot()
    {
        var document = ContentArtifactCurrentStateProjector.ToDocument(
            ActorId,
            new StateEvent { Version = 7, EventId = "event-7" },
            BuildState(DateTimeOffset.Parse("2026-07-20T09:00:00Z")),
            DateTimeOffset.Parse("2026-07-20T10:00:00Z"));
        var queryPort = new ProjectionContentArtifactQueryPort(
            new RecordingDocumentReader(document),
            backingContentPort: null);

        var act = () => queryPort.GetRevisionContentAsync(
            ScopeId,
            ArtifactId,
            "revision-1",
            Principal("revoked-reader"));

        await act.Should().ThrowAsync<ContentArtifactNotFoundException>();
    }

    [Fact]
    public async Task GetRevisionContentAsync_ShouldIdentifyOwnerByPrincipalIdOnly()
    {
        var document = ContentArtifactCurrentStateProjector.ToDocument(
            ActorId,
            new StateEvent { Version = 7, EventId = "event-7" },
            BuildState(DateTimeOffset.Parse("2026-07-20T09:00:00Z")),
            DateTimeOffset.Parse("2026-07-20T10:00:00Z"));
        var queryPort = new ProjectionContentArtifactQueryPort(
            new RecordingDocumentReader(document),
            backingContentPort: null);
        var ownerWithDifferentKind = new ContentArtifactPrincipalContract("owner-1", "service");

        var result = await queryPort.GetRevisionContentAsync(
            ScopeId,
            ArtifactId,
            "revision-1",
            ownerWithDifferentKind);

        result.Reference.RevisionId.Should().Be("revision-1");
    }

    [Fact]
    public async Task GetRevisionContentAsync_ShouldReadAndVerifyBackingContent()
    {
        var document = ContentArtifactCurrentStateProjector.ToDocument(
            ActorId,
            new StateEvent { Version = 7, EventId = "event-7" },
            BuildState(DateTimeOffset.Parse("2026-07-20T09:00:00Z")),
            DateTimeOffset.Parse("2026-07-20T10:00:00Z"));
        var backed = document.Revisions[1];
        backed.InlineContent = ByteString.Empty;
        backed.ContentLocationKind = "backing_object";
        backed.BackingProvider = "workflow-file";
        backed.BackingObjectKey = "runs/run-1/revision-2.md";
        var backingContentPort = new RecordingBackingContentPort("revision two");
        var queryPort = new ProjectionContentArtifactQueryPort(
            new RecordingDocumentReader(document),
            backingContentPort);

        var result = await queryPort.GetRevisionContentAsync(
            ScopeId,
            ArtifactId,
            "revision-2",
            Principal("reader-1"));

        result.Content.Should().Equal(ByteString.CopyFromUtf8("revision two").ToByteArray());
        backingContentPort.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ScopeId,
            RunId = "run-1",
        });
        backingContentPort.Requests[0].Reference.Provider.Should().Be("workflow-file");
        backingContentPort.Requests[0].Reference.ObjectKey.Should().Be("runs/run-1/revision-2.md");
    }

    [Fact]
    public async Task GetAndContentRead_ShouldFailClosedForMismatchedOrUnavailableSnapshots()
    {
        var document = ContentArtifactCurrentStateProjector.ToDocument(
            ActorId,
            new StateEvent { Version = 7, EventId = "event-7" },
            BuildState(DateTimeOffset.Parse("2026-07-20T09:00:00Z")),
            DateTimeOffset.Parse("2026-07-20T10:00:00Z"));
        var mismatched = document.Clone();
        mismatched.ScopeId = "scope-other";
        var mismatchedPort = new ProjectionContentArtifactQueryPort(new RecordingDocumentReader(mismatched));

        (await mismatchedPort.GetAsync(ScopeId, ArtifactId)).Should().BeNull();
        var mismatchedRead = () => mismatchedPort.GetRevisionContentAsync(
            ScopeId,
            ArtifactId,
            "revision-1",
            Principal("owner-1"));
        await mismatchedRead.Should().ThrowAsync<ContentArtifactNotFoundException>();

        var tombstoned = document.Clone();
        tombstoned.LifecycleStatus = ContentArtifactLifecycleStatusNames.Tombstoned;
        var tombstonedRead = () => new ProjectionContentArtifactQueryPort(new RecordingDocumentReader(tombstoned))
            .GetRevisionContentAsync(ScopeId, ArtifactId, "revision-1", Principal("owner-1"));
        await tombstonedRead.Should().ThrowAsync<ContentArtifactContentUnavailableException>()
            .WithMessage("*artifact is tombstoned*");

        var missingRevisionRead = () => new ProjectionContentArtifactQueryPort(new RecordingDocumentReader(document))
            .GetRevisionContentAsync(ScopeId, ArtifactId, "revision-missing", Principal("owner-1"));
        await missingRevisionRead.Should().ThrowAsync<ContentArtifactNotFoundException>();

        var tombstonedMissingRevision = tombstoned.Clone();
        var tombstonedMissingRevisionRead = () => new ProjectionContentArtifactQueryPort(
                new RecordingDocumentReader(tombstonedMissingRevision))
            .GetRevisionContentAsync(ScopeId, ArtifactId, "revision-missing", Principal("owner-1"));
        await tombstonedMissingRevisionRead.Should().ThrowAsync<ContentArtifactNotFoundException>();

        var redacted = document.Clone();
        redacted.Revisions[0].Availability = ContentArtifactRevisionAvailabilityNames.Redacted;
        var redactedRead = () => new ProjectionContentArtifactQueryPort(new RecordingDocumentReader(redacted))
            .GetRevisionContentAsync(ScopeId, ArtifactId, "revision-1", Principal("owner-1"));
        await redactedRead.Should().ThrowAsync<ContentArtifactContentUnavailableException>()
            .WithMessage("*redacted*");

        var expired = document.Clone();
        expired.Revisions[0].Availability = ContentArtifactRevisionAvailabilityNames.RetentionExpired;
        var expiredRead = () => new ProjectionContentArtifactQueryPort(new RecordingDocumentReader(expired))
            .GetRevisionContentAsync(ScopeId, ArtifactId, "revision-1", Principal("owner-1"));
        await expiredRead.Should().ThrowAsync<ContentArtifactContentUnavailableException>()
            .WithMessage("*retention_expired*");

        var missingLocation = document.Clone();
        missingLocation.Revisions[0].ContentLocationKind = string.Empty;
        var missingLocationRead = () => new ProjectionContentArtifactQueryPort(
                new RecordingDocumentReader(missingLocation))
            .GetRevisionContentAsync(ScopeId, ArtifactId, "revision-1", Principal("owner-1"));
        var missingLocationException = (await missingLocationRead.Should()
            .ThrowAsync<InvalidDataException>()).Which;
        missingLocationException.Message.Should().Contain("content location is unavailable");

        var missingProvider = document.Clone();
        missingProvider.Revisions[0].ContentLocationKind = "backing_object";
        var missingProviderRead = () => new ProjectionContentArtifactQueryPort(
                new RecordingDocumentReader(missingProvider))
            .GetRevisionContentAsync(ScopeId, ArtifactId, "revision-1", Principal("owner-1"));
        var missingProviderException = (await missingProviderRead.Should()
            .ThrowAsync<IOException>()).Which;
        missingProviderException.Message.Should().Contain("backing content provider is unavailable");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreEnvelopesWithoutCommittedContentArtifactState()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new ContentArtifactCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-20T10:00:00Z")));

        await projector.ProjectAsync(
            new StudioMaterializationContext
            {
                RootActorId = ActorId,
                ProjectionKind = ContentArtifactGAgent.ProjectionKind,
            },
            new EventEnvelope { Payload = Any.Pack(new StringValue { Value = "not committed state" }) });

        dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public void ToDocument_ShouldMapAllContentArtifactWireVariants()
    {
        var observedAt = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var state = BuildState(observedAt.AddMinutes(-5));
        state.AccessPolicy = null;
        state.RetentionPolicy = null;
        state.Kind = ContentArtifactKind.Text;
        state.LifecycleStatus = ContentArtifactLifecycleStatus.Tombstoned;
        state.Revisions["revision-1"].Availability = ContentArtifactRevisionAvailability.Redacted;
        state.Revisions["revision-1"].Content = null;
        state.Revisions["revision-1"].Provenance = null;
        state.Revisions["revision-2"].Availability = ContentArtifactRevisionAvailability.RetentionExpired;
        state.Revisions["revision-2"].Content = new ContentArtifactRevisionContent
        {
            BackingObject = new ContentArtifactBackingObjectReference
            {
                Provider = "workflow-file",
                ObjectKey = "runs/run-1/revision-2.md",
            },
        };

        var document = ContentArtifactCurrentStateProjector.ToDocument(
            ActorId,
            new StateEvent { Version = 8, EventId = "event-8" },
            state,
            observedAt);

        document.Kind.Should().Be("text");
        document.LifecycleStatus.Should().Be(ContentArtifactLifecycleStatusNames.Tombstoned);
        document.OwnerPrincipalId.Should().BeEmpty();
        document.RetentionPolicyId.Should().BeEmpty();
        document.Revisions[0].Availability.Should().Be(ContentArtifactRevisionAvailabilityNames.Redacted);
        document.Revisions[0].ContentLocationKind.Should().BeEmpty();
        document.Revisions[0].ProvenanceScopeId.Should().BeEmpty();
        document.Revisions[1].Availability.Should().Be(ContentArtifactRevisionAvailabilityNames.RetentionExpired);
        document.Revisions[1].ContentLocationKind.Should().Be("backing_object");

        state.Kind = ContentArtifactKind.StructuredDocument;
        ContentArtifactCurrentStateProjector.ToDocument(ActorId, new StateEvent(), state, observedAt)
            .Kind.Should().Be("structured_document");
        state.Kind = ContentArtifactKind.OtherContent;
        ContentArtifactCurrentStateProjector.ToDocument(ActorId, new StateEvent(), state, observedAt)
            .Kind.Should().Be("other_content");
        state.Kind = ContentArtifactKind.Unspecified;
        state.LifecycleStatus = ContentArtifactLifecycleStatus.Unspecified;
        state.Revisions["revision-1"].Availability = ContentArtifactRevisionAvailability.Unspecified;
        var unspecified = ContentArtifactCurrentStateProjector.ToDocument(
            ActorId,
            new StateEvent(),
            state,
            observedAt);
        unspecified.Kind.Should().BeEmpty();
        unspecified.LifecycleStatus.Should().BeEmpty();
        unspecified.Revisions[0].Availability.Should().BeEmpty();
    }

    [Fact]
    public void CurrentStateDocument_ShouldExposeProjectionIdentityAndAuthoritativeVersion()
    {
        var updatedAt = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        IProjectionReadModel readModel = new ContentArtifactCurrentStateDocument
        {
            Id = ActorId,
            ActorId = ActorId,
            StateVersion = 9,
            LastEventId = "event-9",
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
        };

        readModel.ActorId.Should().Be(ActorId);
        readModel.StateVersion.Should().Be(9);
        readModel.LastEventId.Should().Be("event-9");
        readModel.UpdatedAt.Should().Be(updatedAt);
    }

    private static ContentArtifactState BuildState(DateTimeOffset updatedAt)
    {
        var firstContent = ByteString.CopyFromUtf8("revision one");
        var secondContent = ByteString.CopyFromUtf8("revision two");
        var state = new ContentArtifactState
        {
            ArtifactId = ArtifactId,
            DedupKey = "report-dedup",
            ScopeId = ScopeId,
            TeamId = "team-1",
            Kind = ContentArtifactKind.Markdown,
            Title = "Quarterly report",
            Classification = "internal",
            AccessPolicy = new ContentArtifactAccessPolicy
            {
                Owner = new ContentArtifactPrincipal { PrincipalId = "owner-1", PrincipalKind = "user" },
                ReaderPrincipalIds = { "reader-1" },
                WriterPrincipalIds = { "writer-1" },
            },
            RetentionPolicy = new ContentArtifactRetentionPolicy { PolicyId = "retain-365-days" },
            WorkOrderId = "work-order-1",
            CurrentRevisionId = "revision-2",
            ConcurrencyVersion = 3,
            LifecycleStatus = ContentArtifactLifecycleStatus.Active,
            CreatedAtUtc = Timestamp.FromDateTimeOffset(updatedAt.AddHours(-1)),
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(updatedAt),
        };
        state.Revisions["revision-1"] = new ContentArtifactRevision
        {
            RevisionId = "revision-1",
            RevisionNumber = 1,
            DedupKey = "revision-1-dedup",
            MediaType = "text/markdown",
            ByteLength = firstContent.Length,
            ContentHash = ContentHash("revision one"),
            Content = new ContentArtifactRevisionContent { InlineContent = firstContent },
            Provenance = BuildProvenance(),
            Availability = ContentArtifactRevisionAvailability.Available,
            CreatedAtUtc = Timestamp.FromDateTimeOffset(updatedAt.AddMinutes(-30)),
        };
        state.Revisions["revision-2"] = new ContentArtifactRevision
        {
            RevisionId = "revision-2",
            RevisionNumber = 2,
            DedupKey = "revision-2-dedup",
            ParentRevisionId = "revision-1",
            MediaType = "text/markdown",
            ByteLength = secondContent.Length,
            ContentHash = ContentHash("revision two"),
            Content = new ContentArtifactRevisionContent { InlineContent = secondContent },
            Provenance = BuildProvenance(),
            Availability = ContentArtifactRevisionAvailability.Available,
            CreatedAtUtc = Timestamp.FromDateTimeOffset(updatedAt),
        };
        state.Revisions["revision-2"].Citations.Add(new ContentArtifactCitation
        {
            CitationId = "citation-1",
            ArtifactRevision = new ContentArtifactRevisionCitationSource
            {
                Reference = new ContentArtifactReference
                {
                    ArtifactId = "source-artifact",
                    RevisionId = "source-revision-1",
                    ContentHash = new string('a', 64),
                    MediaType = "text/plain",
                },
            },
        });
        return state;
    }

    private static ContentArtifactExecutionProvenance BuildProvenance() =>
        new()
        {
            ScopeId = ScopeId,
            TeamId = "team-1",
            MemberId = "member-1",
            WorkflowId = "workflow-1",
            PublishedServiceId = "service-1",
            RunId = "run-1",
            WorkOrderId = "work-order-1",
        };

    private static ContentArtifactCurrentStateDocument BuildListDocument(
        string artifactId,
        string ownerPrincipalId,
        IReadOnlyList<string>? readers = null,
        IReadOnlyList<string>? writers = null,
        string lifecycleStatus = ContentArtifactLifecycleStatusNames.Active)
    {
        var actorId = ContentArtifactConventions.BuildActorId(ScopeId, artifactId);
        var timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-20T00:00:00Z"));
        var document = new ContentArtifactCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            StateVersion = 1,
            LastEventId = $"event-{artifactId}",
            UpdatedAt = timestamp.Clone(),
            ArtifactId = artifactId,
            ScopeId = ScopeId,
            Kind = "markdown",
            Title = artifactId,
            Classification = "internal",
            LifecycleStatus = lifecycleStatus,
            OwnerPrincipalId = ownerPrincipalId,
            OwnerPrincipalKind = "user",
            CreatedAtUtc = timestamp.Clone(),
            ArtifactUpdatedAtUtc = timestamp.Clone(),
        };
        document.ReaderPrincipalIds.Add(readers ?? []);
        document.WriterPrincipalIds.Add(writers ?? []);
        return document;
    }

    private static EventEnvelope WrapCommitted(ContentArtifactState state, DateTimeOffset observedAt) =>
        new()
        {
            Id = "envelope-7",
            Timestamp = Timestamp.FromDateTimeOffset(observedAt),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(ActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "event-7",
                    Version = 7,
                    EventData = Any.Pack(new ContentArtifactCurrentRevisionAdvancedEvent()),
                    Timestamp = Timestamp.FromDateTimeOffset(observedAt),
                },
                StateRoot = Any.Pack(state),
            }),
        };

    private static string ContentHash(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(ByteString.CopyFromUtf8(content).Span));

    private static ContentArtifactPrincipalContract Principal(string principalId) =>
        new(principalId, "user");

    private sealed class RecordingWriteDispatcher
        : IProjectionWriteDispatcher<ContentArtifactCurrentStateDocument>
    {
        public List<ContentArtifactCurrentStateDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            ContentArtifactCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());
    }

    private sealed class RecordingDocumentReader(ContentArtifactCurrentStateDocument document)
        : IProjectionDocumentReader<ContentArtifactCurrentStateDocument, string>
    {
        public string? LastKey { get; private set; }
        public ProjectionDocumentQuery? LastQuery { get; private set; }

        public Task<ContentArtifactCurrentStateDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            LastKey = key;
            return Task.FromResult<ContentArtifactCurrentStateDocument?>(key == document.Id ? document : null);
        }

        public Task<ProjectionDocumentQueryResult<ContentArtifactCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(new ProjectionDocumentQueryResult<ContentArtifactCurrentStateDocument>
            {
                Items = [document],
            });
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class MissingBackingContentPort : IContentArtifactBackingContentPort
    {
        public Task<ContentArtifactBackingContentDescriptor> DescribeAsync(
            ContentArtifactBackingContentRequest request,
            CancellationToken ct = default) =>
            throw new FileNotFoundException("missing backing content");

        public Task<Stream> OpenReadAsync(
            ContentArtifactBackingContentRequest request,
            CancellationToken ct = default) =>
            throw new FileNotFoundException("missing backing content");
    }

    private sealed class RecordingBackingContentPort(string content) : IContentArtifactBackingContentPort
    {
        public List<ContentArtifactBackingContentRequest> Requests { get; } = [];

        public Task<ContentArtifactBackingContentDescriptor> DescribeAsync(
            ContentArtifactBackingContentRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(
            ContentArtifactBackingContentRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult<Stream>(new MemoryStream(ByteString.CopyFromUtf8(content).ToByteArray()));
        }
    }
}
