using System.Reflection;
using System.Security.Cryptography;
using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.ContentArtifacts;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests.ContentArtifacts;

public sealed class ContentArtifactGAgentTests
{
    private const string ScopeId = "scope-1";
    private const string DedupKey = "quarterly-report";
    private static readonly string ArtifactId = ContentArtifactConventions.BuildArtifactId(ScopeId, DedupKey);
    private static readonly string ActorId = ContentArtifactConventions.BuildActorId(ScopeId, ArtifactId);

    private static readonly MethodInfo SetIdMethod = typeof(GAgentBase)
        .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GAgentBase.SetId was not found.");

    [Fact]
    public async Task Create_ShouldCommitFirstRevisionAndKeepIdentitiesSeparate()
    {
        var agent = await CreateAgentAsync();
        var command = BuildCreate("initial report");

        await agent.HandleCreateAsync(command);

        agent.State.ArtifactId.Should().Be(ArtifactId);
        agent.State.ScopeId.Should().Be(ScopeId);
        agent.State.TeamId.Should().Be("team-1");
        agent.State.WorkOrderId.Should().Be("work-order-1");
        agent.State.AccessPolicy.Owner.PrincipalId.Should().Be("owner-1");
        agent.State.ConcurrencyVersion.Should().Be(1);
        agent.State.LifecycleStatus.Should().Be(ContentArtifactLifecycleStatus.Active);
        agent.State.CurrentRevisionId.Should().Be(ContentArtifactConventions.BuildRevisionId(ArtifactId, 1));

        var revision = agent.State.Revisions[agent.State.CurrentRevisionId];
        revision.RevisionNumber.Should().Be(1);
        revision.ContentHash.Should().Be(ContentHash("initial report"));
        revision.Content.InlineContent.ToStringUtf8().Should().Be("initial report");
        revision.Provenance.RunId.Should().Be("run-1");
        revision.Provenance.PublishedServiceId.Should().Be("service-1");
    }

    [Fact]
    public async Task Create_ShouldRejectNonCanonicalIdentityAndHashMismatch()
    {
        var command = BuildCreate("initial report");
        command.ArtifactId = "artifact-noncanonical";
        var wrongIdentity = await CreateAgentAsync(actorId: "content-artifact:scope-1:artifact-noncanonical");

        var identityAct = () => wrongIdentity.HandleCreateAsync(command);

        await identityAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*canonical*scope*dedup*");

        command = BuildCreate("initial report");
        command.FirstRevision.ContentHash = new string('0', 64);
        var hashAgent = await CreateAgentAsync();

        var hashAct = () => hashAgent.HandleCreateAsync(command);

        await hashAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*content_hash*does not match*");
    }

    [Fact]
    public async Task DuplicateCreate_ShouldBeIdempotentAndConflictingCreateShouldFailClosed()
    {
        var agent = await CreateAgentAsync();
        var command = BuildCreate("initial report");
        await agent.HandleCreateAsync(command);

        await agent.HandleCreateAsync(command.Clone());

        agent.State.ConcurrencyVersion.Should().Be(1);

        var conflicting = command.Clone();
        conflicting.Title = "Different title";
        var act = () => agent.HandleCreateAsync(conflicting);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*logical identity already exists with a different request*");
        agent.State.Title.Should().Be("Quarterly report");
    }

    [Fact]
    public async Task AppendAndAdvance_ShouldKeepPriorRevisionImmutableAndUseCas()
    {
        var agent = await CreateAgentAsync();
        await agent.HandleCreateAsync(BuildCreate("revision one"));
        var first = agent.State.Revisions[agent.State.CurrentRevisionId].Clone();
        var second = BuildRevision(2, "revision two", "revision-2-dedup", first.RevisionId);

        await agent.HandleAppendRevisionAsync(new AppendContentArtifactRevision
        {
            ArtifactId = ArtifactId,
            ExpectedConcurrencyVersion = 1,
            RequestedBy = Principal("owner-1"),
            Revision = second,
        });

        agent.State.ConcurrencyVersion.Should().Be(2);
        agent.State.CurrentRevisionId.Should().Be(first.RevisionId);
        agent.State.Revisions[first.RevisionId].Should().BeEquivalentTo(first);
        agent.State.Revisions[second.RevisionId].Content.InlineContent.ToStringUtf8().Should().Be("revision two");

        await agent.HandleAdvanceCurrentRevisionAsync(new AdvanceContentArtifactCurrentRevision
        {
            ArtifactId = ArtifactId,
            ExpectedConcurrencyVersion = 2,
            RequestedBy = Principal("owner-1"),
            RevisionId = second.RevisionId,
        });

        agent.State.CurrentRevisionId.Should().Be(second.RevisionId);
        agent.State.ConcurrencyVersion.Should().Be(3);

        var stale = () => agent.HandleAppendRevisionAsync(new AppendContentArtifactRevision
        {
            ArtifactId = ArtifactId,
            ExpectedConcurrencyVersion = 2,
            RequestedBy = Principal("owner-1"),
            Revision = BuildRevision(3, "stale", "revision-3-dedup", second.RevisionId),
        });
        await stale.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*concurrency version is 3, not 2*");
    }

    [Fact]
    public async Task DuplicateAppendAndAdvance_ShouldBeIdempotentAfterVersionMoves()
    {
        var agent = await CreateAgentAsync();
        await agent.HandleCreateAsync(BuildCreate("revision one"));
        var second = BuildRevision(2, "revision two", "revision-2-dedup", agent.State.CurrentRevisionId);
        var append = new AppendContentArtifactRevision
        {
            ArtifactId = ArtifactId,
            ExpectedConcurrencyVersion = 1,
            RequestedBy = Principal("owner-1"),
            Revision = second,
        };
        await agent.HandleAppendRevisionAsync(append);
        await agent.HandleAdvanceCurrentRevisionAsync(new AdvanceContentArtifactCurrentRevision
        {
            ArtifactId = ArtifactId,
            ExpectedConcurrencyVersion = 2,
            RequestedBy = Principal("owner-1"),
            RevisionId = second.RevisionId,
        });

        await agent.HandleAppendRevisionAsync(append.Clone());
        await agent.HandleAdvanceCurrentRevisionAsync(new AdvanceContentArtifactCurrentRevision
        {
            ArtifactId = ArtifactId,
            ExpectedConcurrencyVersion = 2,
            RequestedBy = Principal("owner-1"),
            RevisionId = second.RevisionId,
        });

        agent.State.ConcurrencyVersion.Should().Be(3);
        agent.State.Revisions.Should().HaveCount(2);
    }

    [Fact]
    public async Task Restart_ShouldRecoverRevisionHistoryAndContinueFromAuthoritativeVersion()
    {
        var store = new InMemoryEventStore();
        var original = await CreateAgentAsync(eventStore: store);
        await original.HandleCreateAsync(BuildCreate("revision one"));
        var second = BuildRevision(2, "revision two", "revision-2-dedup", original.State.CurrentRevisionId);
        await original.HandleAppendRevisionAsync(new AppendContentArtifactRevision
        {
            ArtifactId = ArtifactId,
            ExpectedConcurrencyVersion = 1,
            RequestedBy = Principal("owner-1"),
            Revision = second,
        });

        var recovered = await CreateAgentAsync(eventStore: store);

        recovered.State.Revisions.Should().HaveCount(2);
        recovered.State.ConcurrencyVersion.Should().Be(2);
        await recovered.HandleAdvanceCurrentRevisionAsync(new AdvanceContentArtifactCurrentRevision
        {
            ArtifactId = ArtifactId,
            ExpectedConcurrencyVersion = 2,
            RequestedBy = Principal("owner-1"),
            RevisionId = second.RevisionId,
        });
        recovered.State.CurrentRevisionId.Should().Be(second.RevisionId);
    }

    [Fact]
    public async Task Citation_ShouldRequireExactRevisionOrStableExternalIdentity()
    {
        var agent = await CreateAgentAsync();
        var command = BuildCreate("cited report");
        command.FirstRevision.Citations.Add(new ContentArtifactCitation
        {
            CitationId = "citation-1",
            Label = "source report",
            ArtifactRevision = new ContentArtifactRevisionCitationSource
            {
                Reference = new ContentArtifactReference
                {
                    ArtifactId = "source-artifact",
                    RevisionId = "source-revision-3",
                    ContentHash = new string('a', 64),
                    MediaType = "text/markdown",
                },
            },
            Locator = new ContentArtifactCitationLocator { Section = "results" },
        });

        await agent.HandleCreateAsync(command);

        var citation = agent.State.Revisions[agent.State.CurrentRevisionId].Citations.Should().ContainSingle().Subject;
        citation.ArtifactRevision.Reference.RevisionId.Should().Be("source-revision-3");
        citation.Locator.Section.Should().Be("results");

        var invalid = BuildRevision(2, "bad citation", "revision-2-dedup", agent.State.CurrentRevisionId);
        invalid.Citations.Add(new ContentArtifactCitation
        {
            CitationId = "citation-invalid",
            ArtifactRevision = new ContentArtifactRevisionCitationSource
            {
                Reference = new ContentArtifactReference { ArtifactId = "source-artifact" },
            },
        });
        var act = () => agent.HandleAppendRevisionAsync(new AppendContentArtifactRevision
        {
            ArtifactId = ArtifactId,
            ExpectedConcurrencyVersion = 1,
            RequestedBy = Principal("owner-1"),
            Revision = invalid,
        });
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*citation*revision_id*required*");
    }

    [Fact]
    public async Task RedactionExpiryAndTombstone_ShouldPreserveProvenanceWithoutServingContent()
    {
        var agent = await CreateAgentAsync();
        await agent.HandleCreateAsync(BuildCreate("sensitive report"));
        var firstRevisionId = agent.State.CurrentRevisionId;

        await agent.HandleRedactRevisionAsync(new RedactContentArtifactRevision
        {
            ArtifactId = ArtifactId,
            ExpectedConcurrencyVersion = 1,
            RequestedBy = Principal("owner-1"),
            RevisionId = firstRevisionId,
            Reason = "privacy request",
        });

        var redacted = agent.State.Revisions[firstRevisionId];
        redacted.Availability.Should().Be(ContentArtifactRevisionAvailability.Redacted);
        redacted.Content.LocationCase.Should().Be(ContentArtifactRevisionContent.LocationOneofCase.None);
        redacted.ContentHash.Should().Be(ContentHash("sensitive report"));
        redacted.Provenance.RunId.Should().Be("run-1");

        var second = BuildRevision(2, "temporary report", "revision-2-dedup", firstRevisionId);
        await agent.HandleAppendRevisionAsync(new AppendContentArtifactRevision
        {
            ArtifactId = ArtifactId,
            ExpectedConcurrencyVersion = 2,
            RequestedBy = Principal("owner-1"),
            Revision = second,
        });
        await agent.HandleExpireRevisionAsync(new ExpireContentArtifactRevision
        {
            ArtifactId = ArtifactId,
            ExpectedConcurrencyVersion = 3,
            RequestedBy = Principal("owner-1"),
            RevisionId = second.RevisionId,
        });
        agent.State.Revisions[second.RevisionId].Availability.Should()
            .Be(ContentArtifactRevisionAvailability.RetentionExpired);

        await agent.HandleTombstoneAsync(new TombstoneContentArtifact
        {
            ArtifactId = ArtifactId,
            ExpectedConcurrencyVersion = 4,
            RequestedBy = Principal("owner-1"),
            Reason = "retention complete",
        });

        agent.State.LifecycleStatus.Should().Be(ContentArtifactLifecycleStatus.Tombstoned);
        agent.State.CurrentRevisionId.Should().BeEmpty();
        agent.State.Revisions.Should().HaveCount(2);
        agent.State.Revisions.Values.Should().OnlyContain(revision => revision.Provenance.RunId == "run-1");
    }

    [Fact]
    public async Task BackingObject_ShouldBeVerifiedAndUnavailableProviderShouldFailClosed()
    {
        var content = "backed report";
        var backing = new ContentArtifactBackingObjectReference
        {
            Provider = "object-store",
            ObjectKey = "scope-1/reports/1",
        };
        var command = BuildCreate(content);
        command.FirstRevision.Content = new ContentArtifactRevisionContent
        {
            BackingObject = backing,
        };
        var port = new RecordingBackingContentPort(content);
        var agent = await CreateAgentAsync(backingContentPort: port);

        await agent.HandleCreateAsync(command);

        var described = port.Described.Should().ContainSingle().Subject;
        described.Reference.Should().BeEquivalentTo(backing);
        described.ScopeId.Should().Be(ScopeId);
        described.RunId.Should().Be("run-1");
        agent.State.Revisions[agent.State.CurrentRevisionId].Content.BackingObject.ObjectKey.Should()
            .Be("scope-1/reports/1");

        var unavailable = await CreateAgentAsync();
        var act = () => unavailable.HandleCreateAsync(command.Clone());
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*backing content port*not registered*");
    }

    private static async Task<ContentArtifactGAgent> CreateAgentAsync(
        string? actorId = null,
        InMemoryEventStore? eventStore = null,
        IContentArtifactBackingContentPort? backingContentPort = null)
    {
        var agent = new ContentArtifactGAgent(backingContentPort)
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ContentArtifactState>(
                eventStore ?? new InMemoryEventStore()),
            Services = new ServiceCollection()
                .AddSingleton<IEnumerable<IGAgentExecutionHook>>([])
                .BuildServiceProvider(),
        };
        SetIdMethod.Invoke(agent, [actorId ?? ActorId]);
        await agent.ActivateAsync();
        return agent;
    }

    private static CreateContentArtifact BuildCreate(string content)
    {
        var command = new CreateContentArtifact
        {
            ArtifactId = ArtifactId,
            DedupKey = DedupKey,
            ScopeId = ScopeId,
            TeamId = "team-1",
            Kind = ContentArtifactKind.Markdown,
            Title = "Quarterly report",
            Classification = "internal",
            AccessPolicy = new ContentArtifactAccessPolicy
            {
                Owner = Principal("owner-1"),
                ReaderPrincipalIds = { "reader-1" },
                WriterPrincipalIds = { "writer-1" },
            },
            RetentionPolicy = new ContentArtifactRetentionPolicy
            {
                PolicyId = "retain-365-days",
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2027-07-20T00:00:00Z")),
            },
            WorkOrderId = "work-order-1",
            ExpectedConcurrencyVersion = 0,
        };
        command.FirstRevision = BuildRevision(1, content, "revision-1-dedup");
        return command;
    }

    private static ContentArtifactRevision BuildRevision(
        long number,
        string content,
        string dedupKey,
        string? parentRevisionId = null) =>
        new()
        {
            RevisionId = ContentArtifactConventions.BuildRevisionId(ArtifactId, number),
            RevisionNumber = number,
            DedupKey = dedupKey,
            ParentRevisionId = parentRevisionId ?? string.Empty,
            MediaType = "text/markdown",
            ByteLength = ByteString.CopyFromUtf8(content).Length,
            ContentHash = ContentHash(content),
            Content = new ContentArtifactRevisionContent
            {
                InlineContent = ByteString.CopyFromUtf8(content),
            },
            Provenance = new ContentArtifactExecutionProvenance
            {
                ScopeId = ScopeId,
                TeamId = "team-1",
                MemberId = "member-1",
                WorkflowId = "workflow-1",
                PublishedServiceId = "service-1",
                RunId = "run-1",
                WorkOrderId = "work-order-1",
            },
            Availability = ContentArtifactRevisionAvailability.Available,
        };

    private static ContentArtifactPrincipal Principal(string principalId) =>
        new()
        {
            PrincipalId = principalId,
            PrincipalKind = "user",
        };

    private static string ContentHash(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(ByteString.CopyFromUtf8(content).Span));

    private sealed class RecordingBackingContentPort(string content) : IContentArtifactBackingContentPort
    {
        public List<ContentArtifactBackingContentRequest> Described { get; } = [];

        public Task<ContentArtifactBackingContentDescriptor> DescribeAsync(
            ContentArtifactBackingContentRequest request,
            CancellationToken ct = default)
        {
            Described.Add(request with { Reference = request.Reference.Clone() });
            return Task.FromResult(new ContentArtifactBackingContentDescriptor(
                ByteString.CopyFromUtf8(content).Length,
                ContentHash(content)));
        }

        public Task<Stream> OpenReadAsync(
            ContentArtifactBackingContentRequest request,
            CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(ByteString.CopyFromUtf8(content).ToByteArray()));
    }
}
