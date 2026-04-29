using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.CommandServices;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class ActorDispatchStudioTeamCommandServiceTests
{
    private const string ScopeId = "scope-1";

    [Fact]
    public async Task CreateAsync_ShouldDispatchCreatedEventToCanonicalActor()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioTeamCommandService(bootstrap, dispatch);

        var summary = await service.CreateAsync(
            ScopeId,
            new CreateStudioTeamRequest(
                DisplayName: "Team Alpha",
                Description: "desc",
                TeamId: "t-alpha"),
            CancellationToken.None);

        summary.TeamId.Should().Be("t-alpha");
        summary.ScopeId.Should().Be(ScopeId);
        summary.DisplayName.Should().Be("Team Alpha");
        summary.Description.Should().Be("desc");
        summary.LifecycleStage.Should().Be(TeamLifecycleStageNames.Active);
        summary.MemberCount.Should().Be(0);

        bootstrap.EnsuredActorIds.Should().ContainSingle()
            .Which.Should().Be("studio-team:scope-1:t-alpha");
        dispatch.Dispatches.Should().ContainSingle();

        var dispatched = dispatch.Dispatches[0];
        dispatched.ActorId.Should().Be("studio-team:scope-1:t-alpha");
        dispatched.Envelope.Payload.Is(StudioTeamCreatedEvent.Descriptor).Should().BeTrue();
        var evt = dispatched.Envelope.Payload.Unpack<StudioTeamCreatedEvent>();
        evt.TeamId.Should().Be("t-alpha");
        evt.ScopeId.Should().Be(ScopeId);
        evt.DisplayName.Should().Be("Team Alpha");
        evt.Description.Should().Be("desc");
    }

    [Fact]
    public async Task CreateAsync_ShouldGenerateTeamId_WhenRequestOmitsIt()
    {
        var service = new ActorDispatchStudioTeamCommandService(
            new RecordingBootstrap(), new RecordingDispatchPort());

        var summary = await service.CreateAsync(
            ScopeId,
            new CreateStudioTeamRequest(DisplayName: "Auto"),
            CancellationToken.None);

        summary.TeamId.Should().StartWith("t-");
        summary.TeamId.Should().NotContain(":");
    }

    [Fact]
    public async Task UpdateAsync_ShouldDispatchUpdatedEvent_WhenDisplayNameChanges()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioTeamCommandService(
            new RecordingBootstrap(), dispatch);

        await service.UpdateAsync(
            ScopeId, "t-1",
            new UpdateStudioTeamRequest(DisplayName: PatchValue<string>.Of("New Name")),
            CancellationToken.None);

        dispatch.Dispatches.Should().ContainSingle();
        var evt = dispatch.Dispatches[0].Envelope.Payload.Unpack<StudioTeamUpdatedEvent>();
        evt.HasDisplayName.Should().BeTrue();
        evt.DisplayName.Should().Be("New Name");
        evt.HasDescription.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_ShouldDispatchUpdatedEvent_WhenDescriptionChanges()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioTeamCommandService(
            new RecordingBootstrap(), dispatch);

        await service.UpdateAsync(
            ScopeId, "t-1",
            new UpdateStudioTeamRequest(Description: PatchValue<string>.Of("new desc")),
            CancellationToken.None);

        dispatch.Dispatches.Should().ContainSingle();
        var evt = dispatch.Dispatches[0].Envelope.Payload.Unpack<StudioTeamUpdatedEvent>();
        evt.HasDescription.Should().BeTrue();
        evt.Description.Should().Be("new desc");
        evt.HasDisplayName.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_ShouldNoOp_WhenNothingToChange()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioTeamCommandService(
            new RecordingBootstrap(), dispatch);

        await service.UpdateAsync(
            ScopeId, "t-1",
            new UpdateStudioTeamRequest(),
            CancellationToken.None);

        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_ShouldDispatchBothFields_WhenBothPresent()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioTeamCommandService(
            new RecordingBootstrap(), dispatch);

        await service.UpdateAsync(
            ScopeId, "t-1",
            new UpdateStudioTeamRequest(
                DisplayName: PatchValue<string>.Of("X"),
                Description: PatchValue<string>.Of("Y")),
            CancellationToken.None);

        dispatch.Dispatches.Should().ContainSingle();
        var evt = dispatch.Dispatches[0].Envelope.Payload.Unpack<StudioTeamUpdatedEvent>();
        evt.HasDisplayName.Should().BeTrue();
        evt.DisplayName.Should().Be("X");
        evt.HasDescription.Should().BeTrue();
        evt.Description.Should().Be("Y");
    }

    [Fact]
    public async Task ArchiveAsync_ShouldDispatchArchivedEvent()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioTeamCommandService(bootstrap, dispatch);

        await service.ArchiveAsync(ScopeId, "t-1", CancellationToken.None);

        bootstrap.EnsuredActorIds.Should().ContainSingle()
            .Which.Should().Be("studio-team:scope-1:t-1");
        dispatch.Dispatches.Should().ContainSingle();

        var evt = dispatch.Dispatches[0].Envelope.Payload.Unpack<StudioTeamArchivedEvent>();
        evt.TeamId.Should().Be("t-1");
        evt.ScopeId.Should().Be(ScopeId);
    }

    [Fact]
    public void Constructor_ShouldRejectNullDependencies()
    {
        FluentActions.Invoking(() =>
                new ActorDispatchStudioTeamCommandService(null!, new RecordingDispatchPort()))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() =>
                new ActorDispatchStudioTeamCommandService(new RecordingBootstrap(), null!))
            .Should().Throw<ArgumentNullException>();
    }

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

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add(new DispatchedCommand(actorId, envelope));
            return Task.CompletedTask;
        }

        public sealed record DispatchedCommand(string ActorId, EventEnvelope Envelope);
    }
}
