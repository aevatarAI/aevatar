using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.CommandServices;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class ActorDispatchStudioMemberReassignTests
{
    private const string ScopeId = "scope-1";

    [Fact]
    public async Task CreateAsync_WithTeamId_ShouldDispatchCreatedThenReassigned()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, dispatch);

        var summary = await service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                MemberId: "m-1",
                TeamId: "t-1"),
            CancellationToken.None);

        summary.TeamId.Should().Be("t-1");

        dispatch.Dispatches.Should().HaveCount(3);

        dispatch.Dispatches[0].Envelope.Payload.Is(StudioMemberCreatedEvent.Descriptor).Should().BeTrue();
        var created = dispatch.Dispatches[0].Envelope.Payload.Unpack<StudioMemberCreatedEvent>();
        created.MemberId.Should().Be("m-1");

        dispatch.Dispatches[1].Envelope.Payload.Is(StudioMemberReassignedEvent.Descriptor).Should().BeTrue();
        var reassigned = dispatch.Dispatches[1].Envelope.Payload.Unpack<StudioMemberReassignedEvent>();
        reassigned.HasFromTeamId.Should().BeFalse();
        reassigned.ToTeamId.Should().Be("t-1");

        dispatch.Dispatches[2].Envelope.Payload.Is(StudioMemberReassignedEvent.Descriptor).Should().BeTrue();
        dispatch.Dispatches[2].ActorId.Should().StartWith("studio-team:");
    }

    [Fact]
    public async Task CreateAsync_WithoutTeamId_ShouldNotDispatchReassignment()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(
            new RecordingBootstrap(), dispatch);

        var summary = await service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                MemberId: "m-1"),
            CancellationToken.None);

        summary.TeamId.Should().BeNull();

        dispatch.Dispatches.Should().ContainSingle();
        dispatch.Dispatches[0].Envelope.Payload.Is(StudioMemberCreatedEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task ReassignTeamAsync_ShouldDispatchToMemberAndTeams()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, dispatch);

        await service.ReassignTeamAsync(
            ScopeId, "m-1",
            fromTeamId: "t-old",
            toTeamId: "t-new",
            CancellationToken.None);

        dispatch.Dispatches.Should().HaveCount(3);

        dispatch.Dispatches[0].ActorId.Should().Be("studio-member:scope-1:m-1");
        var evt = dispatch.Dispatches[0].Envelope.Payload.Unpack<StudioMemberReassignedEvent>();
        evt.FromTeamId.Should().Be("t-old");
        evt.ToTeamId.Should().Be("t-new");

        dispatch.Dispatches[1].ActorId.Should().Be("studio-team:scope-1:t-old");
        dispatch.Dispatches[2].ActorId.Should().Be("studio-team:scope-1:t-new");
    }

    [Fact]
    public async Task ReassignTeamAsync_PureAssign_ShouldDispatchToMemberAndDestTeam()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(
            new RecordingBootstrap(), dispatch);

        await service.ReassignTeamAsync(
            ScopeId, "m-1",
            fromTeamId: null,
            toTeamId: "t-new",
            CancellationToken.None);

        dispatch.Dispatches.Should().HaveCount(2);
        dispatch.Dispatches[0].ActorId.Should().Be("studio-member:scope-1:m-1");
        dispatch.Dispatches[1].ActorId.Should().Be("studio-team:scope-1:t-new");

        var evt = dispatch.Dispatches[0].Envelope.Payload.Unpack<StudioMemberReassignedEvent>();
        evt.HasFromTeamId.Should().BeFalse();
        evt.ToTeamId.Should().Be("t-new");
    }

    [Fact]
    public async Task ReassignTeamAsync_PureUnassign_ShouldDispatchToMemberAndSourceTeam()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(
            new RecordingBootstrap(), dispatch);

        await service.ReassignTeamAsync(
            ScopeId, "m-1",
            fromTeamId: "t-old",
            toTeamId: null,
            CancellationToken.None);

        dispatch.Dispatches.Should().HaveCount(2);
        dispatch.Dispatches[0].ActorId.Should().Be("studio-member:scope-1:m-1");
        dispatch.Dispatches[1].ActorId.Should().Be("studio-team:scope-1:t-old");

        var evt = dispatch.Dispatches[0].Envelope.Payload.Unpack<StudioMemberReassignedEvent>();
        evt.FromTeamId.Should().Be("t-old");
        evt.HasToTeamId.Should().BeFalse();
    }

    [Fact]
    public void ReassignTeamAsync_BothNull_ShouldReject()
    {
        var service = new ActorDispatchStudioMemberCommandService(
            new RecordingBootstrap(), new RecordingDispatchPort());

        var act = () => service.ReassignTeamAsync(
            ScopeId, "m-1", fromTeamId: null, toTeamId: null);

        act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at least one*");
    }

    [Fact]
    public void ReassignTeamAsync_BothEqual_ShouldReject()
    {
        var service = new ActorDispatchStudioMemberCommandService(
            new RecordingBootstrap(), new RecordingDispatchPort());

        var act = () => service.ReassignTeamAsync(
            ScopeId, "m-1", fromTeamId: "t-same", toTeamId: "t-same");

        act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must differ*");
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

        public Task<IActor?> GetExistingAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor =>
            Task.FromResult<IActor?>(new StubActor(actorId));

        public Task<IActor?> GetExistingActorAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor =>
            Task.FromResult<IActor?>(new StubActor(actorId));
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
