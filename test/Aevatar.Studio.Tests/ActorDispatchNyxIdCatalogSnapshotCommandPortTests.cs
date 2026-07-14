using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Application.Authorization;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.CommandServices;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class ActorDispatchNyxIdCatalogSnapshotCommandPortTests
{
    [Fact]
    public async Task LifecycleOperations_ShouldUseStableOwnerIsolationAndMapCompletePayloads()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var port = new ActorDispatchNyxIdCatalogSnapshotCommandPort(bootstrap, CreateCommandDispatch(dispatch));
        var personal = Owner(" personal-alpha ", NyxIdCatalogOwnerKind.Personal);
        var organization = Owner("org-alpha", NyxIdCatalogOwnerKind.Organization);
        var observation = new NyxIdCatalogObservation(
            personal,
            DateTimeOffset.Parse("2026-07-15T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-15T00:15:00Z"),
            "revision-alpha",
            "digest-alpha",
            [ServiceGrant()]);

        await port.ObserveAsync(observation);
        await port.RecordRefreshFailureAsync(personal, DateTimeOffset.Parse("2026-07-15T00:16:00Z"), "timeout");
        await port.InvalidateAsync(organization, DateTimeOffset.Parse("2026-07-15T00:17:00Z"), "access_lost");

        bootstrap.ActorIds[0].Should().Be(bootstrap.ActorIds[1]);
        bootstrap.ActorIds[2].Should().NotBe(bootstrap.ActorIds[0]);
        bootstrap.ActorIds.Should().OnlyContain(static actorId =>
            actorId.StartsWith("studio-nyxid-catalog-snapshot:", StringComparison.Ordinal));
        dispatch.ActorIds.Should().Equal(bootstrap.ActorIds);
        dispatch.Envelopes.Should().OnlyContain(static envelope =>
            envelope.Route.PublisherActorId == "aevatar.studio.nyxid-catalog-lifecycle");
        var observed = dispatch.Envelopes[0].Payload.Unpack<ObserveNyxIdCatalogSnapshotCommand>();
        observed.Owner.OwnerSubject.Should().Be("personal-alpha");
        observed.Owner.Authority.Should().Be("https://nyx.example");
        observed.ObservedAt.ToDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-07-15T00:00:00Z"));
        observed.FreshUntil.ToDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-07-15T00:15:00Z"));
        observed.ExternalRevision.Should().Be("revision-alpha");
        observed.ContentDigest.Should().Be("digest-alpha");
        var service = observed.Services.Should().ContainSingle().Subject;
        service.UserServiceId.Should().Be("user-service-alpha");
        service.ServiceSlug.Should().Be("calendar");
        service.DisplayName.Should().Be("Calendar");
        service.Reachable.Should().BeTrue();
        var node = service.Nodes.Should().ContainSingle().Subject;
        node.NodeId.Should().Be("node-alpha");
        node.Primary.Should().BeTrue();
        var failure = dispatch.Envelopes[1].Payload.Unpack<RecordNyxIdCatalogSnapshotRefreshFailureCommand>();
        failure.Owner.OwnerSubject.Should().Be("personal-alpha");
        failure.FailedAt.ToDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-07-15T00:16:00Z"));
        failure.FailureCode.Should().Be("timeout");
        var invalidation = dispatch.Envelopes[2].Payload.Unpack<InvalidateNyxIdCatalogSnapshotCommand>();
        invalidation.Owner.OwnerSubject.Should().Be("org-alpha");
        invalidation.InvalidatedAt.ToDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-07-15T00:17:00Z"));
        invalidation.Reason.Should().Be("access_lost");
    }

    private static NyxIdCatalogOwnerIdentity Owner(string subject, NyxIdCatalogOwnerKind kind) => new()
    {
        Authority = "https://nyx.example",
        OwnerKind = kind,
        OwnerSubject = subject,
    };

    private static NyxIdServiceGrant ServiceGrant()
    {
        var grant = new NyxIdServiceGrant
        {
            UserServiceId = "user-service-alpha",
            ServiceSlug = "calendar",
            DisplayName = "Calendar",
        };
        grant.NodeGrants.Add(new NyxIdNodeGrant { NodeId = "node-alpha", Primary = true });
        return grant;
    }

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

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<string> ActorIds { get; } = [];
        public List<EventEnvelope> Envelopes { get; } = [];
        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ActorIds.Add(actorId);
            Envelopes.Add(envelope);
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private static StudioProjectionActorCommandDispatch CreateCommandDispatch(IActorDispatchPort dispatchPort)
    {
        var service = new Aevatar.CQRS.Core.Commands.DefaultCommandDispatchService<
            StudioProjectionActorCommand, StudioProjectionActorCommandTarget,
            StudioProjectionActorCommandReceipt, StudioProjectionActorCommandStartError>(
            new Aevatar.CQRS.Core.Commands.DefaultCommandDispatchPipeline<
                StudioProjectionActorCommand, StudioProjectionActorCommandTarget,
                StudioProjectionActorCommandReceipt, StudioProjectionActorCommandStartError>(
                new StudioProjectionActorCommandTargetResolver(),
                new Aevatar.CQRS.Core.Commands.DefaultCommandContextPolicy(),
                new StudioProjectionActorCommandEnvelopeFactory(),
                new Aevatar.CQRS.Core.Commands.ActorCommandTargetDispatcher<StudioProjectionActorCommandTarget>(dispatchPort),
                new StudioProjectionActorCommandReceiptFactory()));
        return new StudioProjectionActorCommandDispatch(service);
    }
}
