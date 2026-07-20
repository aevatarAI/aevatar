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
    public async Task CreateAsync_ShouldUseStableArtifactRevisionAndRuntimeDedupIdentities()
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
        dispatchPort.Envelopes.Select(static envelope => envelope.EnsureRuntime().EnsureDeduplication().OperationId)
            .Should().OnlyContain(id => id == first.CommandId);
    }

    [Fact]
    public async Task AppendRevisionAsync_ShouldUseServerDerivedRevisionNumberIndependentlyFromExpectedVersion()
    {
        var dispatchPort = new RecordingDispatchPort();
        var service = new ActorDispatchContentArtifactCommandService(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatchPort));
        var artifactId = ContentArtifactConventions.BuildArtifactId(ScopeId, "report-dedup");
        var request = new AppendContentArtifactRevisionRequest(
            ExpectedConcurrencyVersion: 7,
            Revision: RevisionWrite("revision four", "revision-4-dedup", "revision-3"));

        var receipt = await service.AppendRevisionAsync(
            ScopeId,
            artifactId,
            revisionNumber: 4,
            request,
            new ContentArtifactPrincipalContract("owner-1", "user"));

        receipt.CommandId.Should().StartWith($"content-artifact-append-{artifactId}-v7-");
        var command = dispatchPort.Envelopes.Should().ContainSingle().Subject.Payload!
            .Unpack<Aevatar.ContentArtifacts.Abstractions.AppendContentArtifactRevision>();
        command.Revision.RevisionNumber.Should().Be(4);
        command.Revision.RevisionId.Should().Be(ContentArtifactConventions.BuildRevisionId(artifactId, 4));
        command.Revision.ParentRevisionId.Should().Be("revision-3");
    }

    private static CreateContentArtifactRequest CreateRequest() =>
        new(
            TeamId: "team-1",
            Kind: "markdown",
            Title: "Quarterly report",
            Classification: "internal",
            DedupKey: "report-dedup",
            FirstRevision: RevisionWrite("report", "revision-1-dedup"));

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
