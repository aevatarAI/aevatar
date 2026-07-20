using System.Security.Cryptography;
using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ContentArtifacts;
using Aevatar.Studio.Hosting;
using Aevatar.Studio.Application.Studio.Contracts;
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
    public async Task QueryPort_ShouldFilterByScopeOwnerAndResolveExactRevision()
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
        reader.LastQuery.Filters.Should().Contain(filter => filter.FieldPath == "owner_principal_id");
        reader.LastQuery.Filters.Should().Contain(filter => filter.FieldPath == "team_id");
        reader.LastQuery.Filters.Should().Contain(filter => filter.FieldPath == "kind");
        reader.LastQuery.Filters.Should().Contain(filter => filter.FieldPath == "provenance_run_ids");
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
    public async Task GetRevisionContentAsync_ShouldFailClosedForMissingBackingContent()
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

        await act.Should().ThrowAsync<ContentArtifactContentUnavailableException>()
            .WithMessage("*missing backing content*");
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

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not authorized to read*");
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
        public ProjectionDocumentQuery? LastQuery { get; private set; }

        public Task<ContentArtifactCurrentStateDocument?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<ContentArtifactCurrentStateDocument?>(key == document.Id ? document : null);

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
}
