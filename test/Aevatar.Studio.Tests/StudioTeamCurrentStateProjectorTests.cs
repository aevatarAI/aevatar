using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class StudioTeamCurrentStateProjectorTests
{
    private const string RootActorId = "studio-team:scope-1:t-1";

    [Fact]
    public async Task ProjectAsync_ShouldUpsertDocument_WhenCommittedStateEventArrives()
    {
        var dispatcher = new RecordingWriteDispatcher<StudioTeamCurrentStateDocument>();
        var clock = new FixedProjectionClock(DateTimeOffset.Parse("2026-04-28T00:00:00Z"));
        var projector = new StudioTeamCurrentStateProjector(dispatcher, clock);

        var state = new StudioTeamState
        {
            TeamId = "t-1",
            ScopeId = "scope-1",
            DisplayName = "Team Alpha",
            Description = "alpha desc",
            LifecycleStage = StudioTeamLifecycleStage.Active,
            CreatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            UpdatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        };
        state.MemberIds.Add("m-1");
        state.MemberIds.Add("m-2");

        var envelope = WrapCommitted(
            payload: new StudioTeamCreatedEvent { TeamId = "t-1" },
            state: state,
            version: 3,
            eventId: "evt-3");

        await projector.ProjectAsync(NewContext(), envelope);

        dispatcher.Upserts.Should().ContainSingle();
        var written = dispatcher.Upserts[0];

        written.Id.Should().Be(RootActorId);
        written.ActorId.Should().Be(RootActorId);
        written.StateVersion.Should().Be(3);
        written.LastEventId.Should().Be("evt-3");

        written.TeamId.Should().Be("t-1");
        written.ScopeId.Should().Be("scope-1");
        written.DisplayName.Should().Be("Team Alpha");
        written.Description.Should().Be("alpha desc");
        written.LifecycleStage.Should().Be(TeamLifecycleStageNames.Active);
        written.MemberCount.Should().Be(2);
    }

    [Fact]
    public async Task ProjectAsync_ShouldDeriveCountFromRoster()
    {
        var dispatcher = new RecordingWriteDispatcher<StudioTeamCurrentStateDocument>();
        var projector = new StudioTeamCurrentStateProjector(
            dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));

        var state = new StudioTeamState
        {
            TeamId = "t-1",
            ScopeId = "scope-1",
            DisplayName = "Empty Team",
            LifecycleStage = StudioTeamLifecycleStage.Active,
            CreatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            UpdatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(new StudioTeamCreatedEvent(), state, 1, "evt-1"));

        dispatcher.Upserts[0].MemberCount.Should().Be(0);
    }

    [Fact]
    public async Task ProjectAsync_ShouldMapArchivedLifecycle()
    {
        var dispatcher = new RecordingWriteDispatcher<StudioTeamCurrentStateDocument>();
        var projector = new StudioTeamCurrentStateProjector(
            dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));

        var state = new StudioTeamState
        {
            TeamId = "t-1",
            ScopeId = "scope-1",
            DisplayName = "Archived Team",
            LifecycleStage = StudioTeamLifecycleStage.Archived,
            CreatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            UpdatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(new StudioTeamArchivedEvent(), state, 2, "evt-2"));

        dispatcher.Upserts[0].LifecycleStage.Should().Be(TeamLifecycleStageNames.Archived);
    }

    [Fact]
    public async Task ProjectAsync_ShouldNoOp_WhenPayloadIsNotCommittedStateEvent()
    {
        var dispatcher = new RecordingWriteDispatcher<StudioTeamCurrentStateDocument>();
        var clock = new FixedProjectionClock(DateTimeOffset.UtcNow);
        var projector = new StudioTeamCurrentStateProjector(dispatcher, clock);

        var envelope = new EventEnvelope
        {
            Id = "raw",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new StudioTeamCreatedEvent { TeamId = "t-1" }),
        };

        await projector.ProjectAsync(NewContext(), envelope);

        dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldRejectNullArguments()
    {
        var dispatcher = new RecordingWriteDispatcher<StudioTeamCurrentStateDocument>();
        var clock = new FixedProjectionClock(DateTimeOffset.UtcNow);
        var projector = new StudioTeamCurrentStateProjector(dispatcher, clock);

        await FluentActions
            .Awaiting(() => projector.ProjectAsync(null!, new EventEnvelope()).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions
            .Awaiting(() => projector.ProjectAsync(NewContext(), null!).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ShouldRejectNullDependencies()
    {
        var dispatcher = new RecordingWriteDispatcher<StudioTeamCurrentStateDocument>();
        var clock = new FixedProjectionClock(DateTimeOffset.UtcNow);

        FluentActions
            .Invoking(() => new StudioTeamCurrentStateProjector(null!, clock))
            .Should().Throw<ArgumentNullException>();
        FluentActions
            .Invoking(() => new StudioTeamCurrentStateProjector(dispatcher, null!))
            .Should().Throw<ArgumentNullException>();
    }

    private static StudioMaterializationContext NewContext() => new()
    {
        RootActorId = RootActorId,
        ProjectionKind = "studio-current-state",
    };

    private static EventEnvelope WrapCommitted(
        IMessage payload,
        StudioTeamState state,
        long version,
        string eventId)
    {
        return new EventEnvelope
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
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private sealed class RecordingWriteDispatcher<TReadModel> : IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        public List<TReadModel> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
