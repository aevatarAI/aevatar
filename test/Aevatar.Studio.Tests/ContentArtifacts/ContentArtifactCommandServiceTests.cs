using System.Security.Cryptography;
using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ContentArtifacts;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.CommandServices;
using FluentAssertions;

namespace Aevatar.Studio.Tests.ContentArtifacts;

public sealed class ContentArtifactCommandServiceTests
{
    private const string ScopeId = "scope-1";

    [Fact]
    public async Task CreateAsync_ShouldUseStableArtifactRevisionAndRuntimeDeliveryIdentity()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatchPort = new RecordingDispatchPort();
        var service = new ActorDispatchContentArtifactCommandService(
            bootstrap,
            CreateCommandDispatch(dispatchPort));
        var request = CreateRequest();
        var owner = new ContentArtifactPrincipalContract("owner-1", "user");
        var artifactId = ContentArtifactConventions.BuildArtifactId(ScopeId, request.DedupKey);

        var first = await service.CreateAsync(ScopeId, request, owner);
        var second = await service.CreateAsync(ScopeId, request, owner);

        first.ArtifactId.Should().Be(artifactId);
        first.CommandId.Should().StartWith($"content-artifact-create-{artifactId}-v0-");
        first.CorrelationId.Should().Be(first.CommandId);
        first.Stage.Should().Be(ContentArtifactCommandStageNames.DispatchAccepted);
        second.Should().BeEquivalentTo(first, options => options.Excluding(receipt => receipt.AcceptedAtUtc));
        bootstrap.ActorIds.Should().OnlyContain(id => id == ContentArtifactConventions.BuildActorId(ScopeId, artifactId));
        var command = dispatchPort.Envelopes[0].Payload!.Unpack<Aevatar.ContentArtifacts.Abstractions.CreateContentArtifact>();
        command.FirstRevision.RevisionId.Should().Be(ContentArtifactConventions.BuildRevisionId(artifactId, 1));
        command.FirstRevision.RevisionNumber.Should().Be(1);
        command.FirstRevision.Provenance.ScopeId.Should().Be(ScopeId);
        command.Labels.Should().Contain("period", "2026-08-25");
        dispatchPort.Envelopes.Select(static envelope => envelope.EnsureRuntime().EnsureDeliveryIdentity().OperationId)
            .Should().OnlyContain(id => id == first.CommandId);
    }

    [Fact]
    public async Task AppendRevisionAsync_ShouldLeaveRevisionIdentityForActor()
    {
        var dispatchPort = new RecordingDispatchPort();
        var service = new ActorDispatchContentArtifactCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatchPort));
        var artifactId = ContentArtifactConventions.BuildArtifactId(ScopeId, "report-dedup");
        var request = new AppendContentArtifactRevisionRequest(
            RevisionWrite("revision four", "revision-4-dedup", "revision-3"));

        var receipt = await service.AppendRevisionAsync(
            ScopeId,
            artifactId,
            request,
            new ContentArtifactPrincipalContract("owner-1", "user"));

        receipt.CommandId.Should().StartWith($"content-artifact-append-{artifactId}-v0-");
        var command = dispatchPort.Envelopes.Should().ContainSingle().Subject.Payload!
            .Unpack<Aevatar.ContentArtifacts.Abstractions.AppendContentArtifactRevision>();
        command.Revision.RevisionNumber.Should().Be(0);
        command.Revision.RevisionId.Should().BeEmpty();
        command.Revision.ParentRevisionId.Should().Be("revision-3");
    }

    [Fact]
    public async Task CreateAsync_ShouldMapBackingContentPoliciesProvenanceAndCitations()
    {
        var dispatchPort = new RecordingDispatchPort();
        var service = new ActorDispatchContentArtifactCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatchPort));
        var publishedAt = DateTimeOffset.Parse("2026-07-19T10:00:00Z");
        var fetchedAt = publishedAt.AddMinutes(5);
        var expiresAt = publishedAt.AddYears(1);
        var request = new CreateContentArtifactRequest(
            TeamId: null,
            Kind: " structured_document ",
            Title: "Research brief",
            Classification: "confidential",
            DedupKey: "research-brief-dedup",
            FirstRevision: new ContentArtifactRevisionWriteRequest(
                DedupKey: "research-brief-revision-1",
                MediaType: "application/vnd.aevatar.document+json",
                ContentHash: new string('A', 64),
                ByteLength: 512,
                Provenance: new ContentArtifactExecutionProvenanceContract(
                    ScopeId,
                    TeamId: "team-1",
                    MemberId: "member-1",
                    WorkflowId: "workflow-1",
                    PublishedServiceId: "service-1",
                    RunId: "run-1",
                    WorkOrderId: "work-order-1"),
                BackingObject: new ContentArtifactBackingObjectContract("workflow-file", "drafts/report.json"),
                Citations:
                [
                    new ContentArtifactCitationContract(
                        "citation-artifact",
                        "Prior revision",
                        new ContentArtifactCitationLocatorContract("summary", 10, 20, "$.summary"),
                        new ContentArtifactReferenceContract(
                            "source-artifact",
                            "source-revision",
                            new string('b', 64),
                            "text/markdown")),
                    new ContentArtifactCitationContract(
                        "citation-external",
                        ExternalSource: new ContentArtifactExternalCitationSourceContract(
                            "https://example.test/source",
                            "source-42",
                            "2026-07-19",
                            new string('c', 64),
                            publishedAt,
                            fetchedAt)),
                ],
                SupersessionReason: "normalized source material"),
            AccessPolicy: new ContentArtifactAccessPolicyContract(["reader-1"], ["writer-1"]),
            RetentionPolicy: new ContentArtifactRetentionPolicyContract("retain-one-year", expiresAt),
            WorkOrderId: "work-order-1");

        await service.CreateAsync(
            ScopeId,
            request,
            new ContentArtifactPrincipalContract("owner-1", "user"));

        var command = dispatchPort.Envelopes.Should().ContainSingle().Subject.Payload!
            .Unpack<Aevatar.ContentArtifacts.Abstractions.CreateContentArtifact>();
        command.TeamId.Should().BeEmpty();
        command.Kind.Should().Be(Aevatar.ContentArtifacts.Abstractions.ContentArtifactKind.StructuredDocument);
        command.AccessPolicy.Owner.PrincipalId.Should().Be("owner-1");
        command.AccessPolicy.ReaderPrincipalIds.Should().Equal("reader-1");
        command.AccessPolicy.WriterPrincipalIds.Should().Equal("writer-1");
        command.RetentionPolicy.PolicyId.Should().Be("retain-one-year");
        command.RetentionPolicy.ExpiresAtUtc.ToDateTimeOffset().Should().Be(expiresAt);
        command.WorkOrderId.Should().Be("work-order-1");
        command.FirstRevision.Content.BackingObject.Provider.Should().Be("workflow-file");
        command.FirstRevision.Content.BackingObject.ObjectKey.Should().Be("drafts/report.json");
        command.FirstRevision.ContentHash.Should().Be(new string('a', 64));
        command.FirstRevision.SupersessionReason.Should().Be("normalized source material");
        command.FirstRevision.Provenance.Should().BeEquivalentTo(new
        {
            ScopeId,
            TeamId = "team-1",
            MemberId = "member-1",
            WorkflowId = "workflow-1",
            PublishedServiceId = "service-1",
            RunId = "run-1",
            WorkOrderId = "work-order-1",
        });
        command.FirstRevision.Citations.Should().HaveCount(2);
        command.FirstRevision.Citations[0].Locator.Section.Should().Be("summary");
        command.FirstRevision.Citations[0].Locator.StartOffset.Should().Be(10);
        command.FirstRevision.Citations[0].Locator.EndOffset.Should().Be(20);
        command.FirstRevision.Citations[0].Locator.Selector.Should().Be("$.summary");
        command.FirstRevision.Citations[0].ArtifactRevision.Reference.ArtifactId.Should().Be("source-artifact");
        command.FirstRevision.Citations[1].ExternalSource.SourceUri.Should().Be("https://example.test/source");
        command.FirstRevision.Citations[1].ExternalSource.PublishedAtUtc.ToDateTimeOffset().Should().Be(publishedAt);
        command.FirstRevision.Citations[1].ExternalSource.FetchedAtUtc.ToDateTimeOffset().Should().Be(fetchedAt);
    }

    [Fact]
    public async Task LifecycleCommands_ShouldMapRequesterConcurrencyAndOperationSpecificFields()
    {
        var dispatchPort = new RecordingDispatchPort();
        var service = new ActorDispatchContentArtifactCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatchPort));
        var artifactId = ContentArtifactConventions.BuildArtifactId(ScopeId, "report-dedup");
        var requester = new ContentArtifactPrincipalContract("writer-1", "service");

        var advanceReceipt = await service.AdvanceCurrentRevisionAsync(
            ScopeId,
            artifactId,
            new AdvanceContentArtifactCurrentRevisionRequest(8, "revision-4"),
            requester);
        var redactReceipt = await service.RedactRevisionAsync(
            ScopeId,
            artifactId,
            "revision-3",
            new RedactContentArtifactRevisionRequest(9, "policy violation"),
            requester);
        var expireReceipt = await service.ExpireRevisionAsync(
            ScopeId,
            artifactId,
            "revision-2",
            new ExpireContentArtifactRevisionRequest(10),
            requester);
        var tombstoneReceipt = await service.TombstoneAsync(
            ScopeId,
            artifactId,
            new TombstoneContentArtifactRequest(11, "retention complete"),
            requester);

        advanceReceipt.CommandId.Should().StartWith($"content-artifact-advance-{artifactId}-v8-");
        redactReceipt.CommandId.Should().StartWith($"content-artifact-redact-{artifactId}-v9-");
        expireReceipt.CommandId.Should().StartWith($"content-artifact-expire-{artifactId}-v10-");
        tombstoneReceipt.CommandId.Should().StartWith($"content-artifact-tombstone-{artifactId}-v11-");
        var advance = dispatchPort.Envelopes[0].Payload!
            .Unpack<Aevatar.ContentArtifacts.Abstractions.AdvanceContentArtifactCurrentRevision>();
        advance.RevisionId.Should().Be("revision-4");
        advance.ExpectedConcurrencyVersion.Should().Be(8);
        advance.RequestedBy.PrincipalId.Should().Be("writer-1");
        advance.RequestedBy.PrincipalKind.Should().Be("service");
        var redact = dispatchPort.Envelopes[1].Payload!
            .Unpack<Aevatar.ContentArtifacts.Abstractions.RedactContentArtifactRevision>();
        redact.RevisionId.Should().Be("revision-3");
        redact.Reason.Should().Be("policy violation");
        var expire = dispatchPort.Envelopes[2].Payload!
            .Unpack<Aevatar.ContentArtifacts.Abstractions.ExpireContentArtifactRevision>();
        expire.RevisionId.Should().Be("revision-2");
        expire.ExpectedConcurrencyVersion.Should().Be(10);
        var tombstone = dispatchPort.Envelopes[3].Payload!
            .Unpack<Aevatar.ContentArtifacts.Abstractions.TombstoneContentArtifact>();
        tombstone.Reason.Should().Be("retention complete");
        tombstone.ExpectedConcurrencyVersion.Should().Be(11);
    }

    [Fact]
    public async Task PinCommands_ShouldDispatchToCanonicalScopeAndPinKeyActor()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatchPort = new RecordingDispatchPort();
        var service = new ActorDispatchContentArtifactPinCommandService(
            bootstrap,
            CreateCommandDispatch(dispatchPort));
        var requester = new ContentArtifactPrincipalContract("owner-1", "user");

        var set = await service.SetAsync(
            ScopeId,
            "daily-ops-report",
            new SetContentArtifactPinRequest("artifact-1", 0, "mutation-1"),
            requester);
        var clear = await service.ClearAsync(
            ScopeId,
            "daily-ops-report",
            new ClearContentArtifactPinRequest(1, "mutation-2"),
            requester);

        bootstrap.ActorIds.Should().OnlyContain(actorId => actorId ==
            ContentArtifactConventions.BuildPinActorId(ScopeId, "daily-ops-report"));
        set.Stage.Should().Be(ContentArtifactCommandStageNames.DispatchAccepted);
        clear.Stage.Should().Be(ContentArtifactCommandStageNames.DispatchAccepted);
        var setCommand = dispatchPort.Envelopes[0].Payload!
            .Unpack<Aevatar.ContentArtifacts.Abstractions.SetContentArtifactPinCommand>();
        setCommand.ArtifactId.Should().Be("artifact-1");
        setCommand.ExpectedPinVersion.Should().Be(0);
        setCommand.MutationId.Should().Be("mutation-1");
        var clearCommand = dispatchPort.Envelopes[1].Payload!
            .Unpack<Aevatar.ContentArtifacts.Abstractions.ClearContentArtifactPinCommand>();
        clearCommand.ExpectedPinVersion.Should().Be(1);
        clearCommand.MutationId.Should().Be("mutation-2");
    }

    [Theory]
    [InlineData("text", Aevatar.ContentArtifacts.Abstractions.ContentArtifactKind.Text)]
    [InlineData("other_content", Aevatar.ContentArtifacts.Abstractions.ContentArtifactKind.OtherContent)]
    public async Task CreateAsync_ShouldMapSupportedKinds(
        string kind,
        Aevatar.ContentArtifacts.Abstractions.ContentArtifactKind expectedKind)
    {
        var dispatchPort = new RecordingDispatchPort();
        var service = new ActorDispatchContentArtifactCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatchPort));

        await service.CreateAsync(
            ScopeId,
            CreateRequest() with { Kind = kind, DedupKey = $"{kind}-dedup" },
            new ContentArtifactPrincipalContract("owner-1", "user"));

        dispatchPort.Envelopes.Should().ContainSingle().Subject.Payload!
            .Unpack<Aevatar.ContentArtifacts.Abstractions.CreateContentArtifact>()
            .Kind.Should().Be(expectedKind);
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectAmbiguousMissingOrUnsupportedRevisionInputs()
    {
        var service = new ActorDispatchContentArtifactCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(new RecordingDispatchPort()));
        var inlineRevision = RevisionWrite("report", "revision-1-dedup");

        var ambiguous = () => service.CreateAsync(
            ScopeId,
            CreateRequest() with
            {
                FirstRevision = inlineRevision with
                {
                    BackingObject = new ContentArtifactBackingObjectContract("workflow-file", "report.md"),
                },
            },
            new ContentArtifactPrincipalContract("owner-1", "user"));
        await ambiguous.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot contain both inline and backing content*");

        var missing = () => service.CreateAsync(
            ScopeId,
            CreateRequest() with
            {
                FirstRevision = inlineRevision with { InlineContent = null },
            },
            new ContentArtifactPrincipalContract("owner-1", "user"));
        await missing.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*content location is required*");

        var unsupported = () => service.CreateAsync(
            ScopeId,
            CreateRequest() with { Kind = "binary" },
            new ContentArtifactPrincipalContract("owner-1", "user"));
        await unsupported.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Unsupported ContentArtifact kind 'binary'.");
    }

    private static CreateContentArtifactRequest CreateRequest() =>
        new(
            TeamId: "team-1",
            Kind: "markdown",
            Title: "Quarterly report",
            Classification: "internal",
            DedupKey: "report-dedup",
            FirstRevision: RevisionWrite("report", "revision-1-dedup"),
            Labels: new Dictionary<string, string> { ["period"] = "2026-08-25" });

    private static ContentArtifactRevisionWriteRequest RevisionWrite(
        string content,
        string dedupKey,
        string? parentRevisionId = null) =>
        new(
            DedupKey: dedupKey,
            MediaType: "text/markdown",
            ContentHash: Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))),
            ByteLength: System.Text.Encoding.UTF8.GetByteCount(content),
            Provenance: new(ScopeId, TeamId: "team-1", PublishedServiceId: "service-1", RunId: "run-1"),
            InlineContent: System.Text.Encoding.UTF8.GetBytes(content),
            ParentRevisionId: parentRevisionId);

    private static StudioProjectionActorCommandDispatch CreateCommandDispatch(IActorDispatchPort dispatchPort) =>
        new(new DefaultCommandDispatchService<
            StudioProjectionActorCommand,
            StudioProjectionActorCommandTarget,
            StudioProjectionActorCommandReceipt,
            StudioProjectionActorCommandStartError>(
            new DefaultCommandDispatchPipeline<
                StudioProjectionActorCommand,
                StudioProjectionActorCommandTarget,
                StudioProjectionActorCommandReceipt,
                StudioProjectionActorCommandStartError>(
                new StudioProjectionActorCommandTargetResolver(),
                new DefaultCommandContextPolicy(),
                new StudioProjectionActorCommandEnvelopeFactory(),
                new ActorCommandTargetDispatcher<StudioProjectionActorCommandTarget>(dispatchPort),
                new StudioProjectionActorCommandReceiptFactory())));

    private sealed class RecordingBootstrap : IStudioActorBootstrap
    {
        public List<string> ActorIds { get; } = [];

        public Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor
        {
            ActorIds.Add(actorId);
            return Task.FromResult<IActor>(new StubActor(actorId));
        }
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<EventEnvelope> Envelopes { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Envelopes.Add(envelope);
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
