using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class StudioTeamRosterFanoutMaterializerTests
{
    private const string RootActorId = "studio-member:scope-1:m-1";

    [Fact]
    public async Task ProjectAsync_ShouldDispatchCommittedReassignmentToAffectedTeamActors()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var materializer = new StudioTeamRosterFanoutMaterializer(
            bootstrap,
            CreateCommandDispatch(dispatch));

        var reassigned = new StudioMemberReassignedEvent
        {
            ScopeId = "scope-1",
            MemberId = "m-1",
            FromTeamId = "t-old",
            ToTeamId = "t-new",
            ReassignedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-25T00:00:00Z")),
        };

        await materializer.ProjectAsync(NewContext(), WrapCommitted(reassigned, version: 7, eventId: "evt-7"));

        bootstrap.EnsuredActorIds.Should().Equal(
            "studio-team:scope-1:t-old",
            "studio-team:scope-1:t-new");
        dispatch.Dispatches.Should().HaveCount(2);
        dispatch.Dispatches.Select(x => x.ActorId).Should().Equal(
            "studio-team:scope-1:t-old",
            "studio-team:scope-1:t-new");
        dispatch.Dispatches.Should().OnlyContain(x =>
            x.Envelope.Payload.Is(StudioMemberReassignedEvent.Descriptor));
        dispatch.Dispatches.Select(x => x.Envelope.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ProjectAsync_ShouldUseStableCommandAndDeliveryOperationIds_ForCommittedEventReplay()
    {
        var dispatch = new RecordingDispatchPort();
        var materializer = new StudioTeamRosterFanoutMaterializer(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatch));
        var envelope = WrapCommitted(new StudioMemberReassignedEvent
        {
            ScopeId = "scope-1",
            MemberId = "m-1",
            ToTeamId = "t-new",
            ReassignedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-25T00:00:00Z")),
        }, version: 8, eventId: "evt-8");

        await materializer.ProjectAsync(NewContext(), envelope);
        await materializer.ProjectAsync(NewContext(), envelope.Clone());

        dispatch.Dispatches.Should().HaveCount(2);
        dispatch.Dispatches[0].ActorId.Should().Be("studio-team:scope-1:t-new");
        dispatch.Dispatches[1].ActorId.Should().Be("studio-team:scope-1:t-new");
        dispatch.Dispatches[1].Envelope.Id.Should().Be(dispatch.Dispatches[0].Envelope.Id);
        dispatch.Dispatches[1].Envelope.Runtime?.DeliveryIdentity?.OperationId
            .Should().Be(dispatch.Dispatches[0].Envelope.Runtime?.DeliveryIdentity?.OperationId);
        dispatch.Dispatches[0].Envelope.Runtime?.DeliveryIdentity?.OperationId
            .Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ProjectAsync_ShouldPropagateDispatchFailure_ForProjectionRetry()
    {
        var materializer = new StudioTeamRosterFanoutMaterializer(
            new RecordingBootstrap(),
            CreateCommandDispatch(new ThrowingDispatchPort()));
        var envelope = WrapCommitted(new StudioMemberReassignedEvent
        {
            ScopeId = "scope-1",
            MemberId = "m-1",
            ToTeamId = "t-new",
            ReassignedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-25T00:00:00Z")),
        }, version: 9, eventId: "evt-9");

        await FluentActions
            .Awaiting(() => materializer.ProjectAsync(NewContext(), envelope).AsTask())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dispatch failed");
    }

    [Fact]
    public async Task ProjectAsync_ShouldNoOp_WhenCommittedEventIsNotReassignment()
    {
        var dispatch = new RecordingDispatchPort();
        var materializer = new StudioTeamRosterFanoutMaterializer(
            new RecordingBootstrap(),
            CreateCommandDispatch(dispatch));

        await materializer.ProjectAsync(
            NewContext(),
            WrapCommitted(new StudioMemberCreatedEvent { MemberId = "m-1" }, version: 1, eventId: "evt-1"));

        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldDispatchDeletedMemberRemovalToPreviousTeam()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var materializer = new StudioTeamRosterFanoutMaterializer(
            bootstrap,
            CreateCommandDispatch(dispatch));
        var deletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-09T06:45:00Z"));

        await materializer.ProjectAsync(
            NewContext(),
            WrapCommitted(new StudioMemberDeletedEvent
            {
                ScopeId = "scope-1",
                MemberId = "m-1",
                PreviousTeamId = "t-old",
                PublishedServiceId = "member-m-1",
                DeletedAtUtc = deletedAt,
            }, version: 10, eventId: "evt-10"));

        bootstrap.EnsuredActorIds.Should().ContainSingle()
            .Which.Should().Be("studio-team:scope-1:t-old");
        var payload = dispatch.Dispatches.Should().ContainSingle().Subject
            .Envelope.Payload.Unpack<StudioMemberReassignedEvent>();
        payload.MemberId.Should().Be("m-1");
        payload.ScopeId.Should().Be("scope-1");
        payload.FromTeamId.Should().Be("t-old");
        payload.HasToTeamId.Should().BeFalse();
        payload.ReassignedAtUtc.Should().Be(deletedAt);
    }

    [Fact]
    public async Task ProjectAsync_ShouldRejectNullArguments()
    {
        var materializer = new StudioTeamRosterFanoutMaterializer(
            new RecordingBootstrap(),
            CreateCommandDispatch(new RecordingDispatchPort()));

        await FluentActions
            .Awaiting(() => materializer.ProjectAsync(null!, new EventEnvelope()).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions
            .Awaiting(() => materializer.ProjectAsync(NewContext(), null!).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ShouldRejectNullDependencies()
    {
        var dispatch = CreateCommandDispatch(new RecordingDispatchPort());

        FluentActions
            .Invoking(() => new StudioTeamRosterFanoutMaterializer(null!, dispatch))
            .Should().Throw<ArgumentNullException>();
        FluentActions
            .Invoking(() => new StudioTeamRosterFanoutMaterializer(new RecordingBootstrap(), null!))
            .Should().Throw<ArgumentNullException>();
    }

    private static StudioMaterializationContext NewContext() => new()
    {
        RootActorId = RootActorId,
        ProjectionKind = "studio-member",
    };

    private static EventEnvelope WrapCommitted(
        IMessage payload,
        long version,
        string eventId) =>
        new()
        {
            Id = eventId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(RootActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    EventData = Any.Pack(payload),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    AgentId = RootActorId,
                },
                StateRoot = Any.Pack(new StudioMemberState
                {
                    ScopeId = "scope-1",
                    MemberId = "m-1",
                }),
            }),
        };

    private sealed class RecordingBootstrap : IStudioActorBootstrap
    {
        public List<string> EnsuredActorIds { get; } = [];

        public Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor
        {
            EnsuredActorIds.Add(actorId);
            return Task.FromResult<IActor>(new StubActor(actorId));
        }
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<DispatchedCommand> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Dispatches.Add(new DispatchedCommand(actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        public sealed record DispatchedCommand(string ActorId, EventEnvelope Envelope);
    }

    private sealed class ThrowingDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("dispatch failed");
    }

    private static StudioProjectionActorCommandDispatch CreateCommandDispatch(IActorDispatchPort dispatchPort)
    {
        var service = new Aevatar.CQRS.Core.Commands.DefaultCommandDispatchService<
            StudioProjectionActorCommand,
            StudioProjectionActorCommandTarget,
            StudioProjectionActorCommandReceipt,
            StudioProjectionActorCommandStartError>(
            new Aevatar.CQRS.Core.Commands.DefaultCommandDispatchPipeline<
                StudioProjectionActorCommand,
                StudioProjectionActorCommandTarget,
                StudioProjectionActorCommandReceipt,
                StudioProjectionActorCommandStartError>(
                new StudioProjectionActorCommandTargetResolver(),
                new DefaultCommandContextPolicy(),
                new StudioProjectionActorCommandEnvelopeFactory(),
                new Aevatar.CQRS.Core.Commands.ActorCommandTargetDispatcher<StudioProjectionActorCommandTarget>(dispatchPort),
                new StudioProjectionActorCommandReceiptFactory()));
        return new StudioProjectionActorCommandDispatch(service);
    }
}
