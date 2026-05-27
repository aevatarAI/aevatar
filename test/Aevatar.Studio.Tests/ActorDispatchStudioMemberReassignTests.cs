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
    public async Task CreateAsync_WithTeamId_ShouldDispatchCreatedThenMemberReassignedOnly()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, CreateCommandDispatch(dispatch));

        var summary = await service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                MemberId: "m-1",
                TeamId: "t-1"),
            CancellationToken.None);

        summary.TeamId.Should().Be("t-1");

        dispatch.Dispatches.Should().HaveCount(2);

        dispatch.Dispatches[0].Envelope.Payload.Is(StudioMemberCreatedEvent.Descriptor).Should().BeTrue();
        var created = dispatch.Dispatches[0].Envelope.Payload.Unpack<StudioMemberCreatedEvent>();
        created.MemberId.Should().Be("m-1");

        dispatch.Dispatches[1].Envelope.Payload.Is(StudioMemberReassignedEvent.Descriptor).Should().BeTrue();
        var reassigned = dispatch.Dispatches[1].Envelope.Payload.Unpack<StudioMemberReassignedEvent>();
        reassigned.HasFromTeamId.Should().BeFalse();
        reassigned.ToTeamId.Should().Be("t-1");

        dispatch.Dispatches.Should().OnlyContain(x => x.ActorId.StartsWith("studio-member:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_WithoutTeamId_ShouldNotDispatchReassignment()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(new RecordingBootstrap(), CreateCommandDispatch(dispatch));

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
    public async Task PatchTeamAssignmentAsync_ShouldDispatchTargetIntentToMember()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, CreateCommandDispatch(dispatch));

        await service.PatchTeamAssignmentAsync(
            ScopeId, "m-1",
            targetTeamId: "t-new",
            CancellationToken.None);

        dispatch.Dispatches.Should().ContainSingle();

        dispatch.Dispatches[0].ActorId.Should().Be("studio-member:scope-1:m-1");
        var evt = dispatch.Dispatches[0].Envelope.Payload.Unpack<StudioMemberTeamAssignmentPatchRequested>();
        evt.ScopeId.Should().Be(ScopeId);
        evt.MemberId.Should().Be("m-1");
        evt.TargetTeamId.Should().Be("t-new");
    }

    [Fact]
    public async Task PatchTeamAssignmentAsync_NullTarget_ShouldDispatchUnassignIntent()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(new RecordingBootstrap(), CreateCommandDispatch(dispatch));

        await service.PatchTeamAssignmentAsync(
            ScopeId, "m-1",
            targetTeamId: null,
            CancellationToken.None);

        dispatch.Dispatches.Should().ContainSingle();
        dispatch.Dispatches[0].ActorId.Should().Be("studio-member:scope-1:m-1");

        var evt = dispatch.Dispatches[0].Envelope.Payload.Unpack<StudioMemberTeamAssignmentPatchRequested>();
        evt.HasTargetTeamId.Should().BeFalse();
    }

    [Fact]
    public async Task PatchTeamAssignmentAsync_ShouldNormalizeTarget()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(new RecordingBootstrap(), CreateCommandDispatch(dispatch));

        await service.PatchTeamAssignmentAsync(
            ScopeId, "m-1",
            targetTeamId: " t-new ",
            CancellationToken.None);

        var evt = dispatch.Dispatches.Single().Envelope.Payload.Unpack<StudioMemberTeamAssignmentPatchRequested>();
        evt.TargetTeamId.Should().Be("t-new");
    }

    [Fact]
    public async Task PatchTeamAssignmentAsync_EmptyTarget_ShouldReject()
    {
        var service = new ActorDispatchStudioMemberCommandService(new RecordingBootstrap(), CreateCommandDispatch(new RecordingDispatchPort()));

        var act = () => service.PatchTeamAssignmentAsync(
            ScopeId, "m-1", targetTeamId: " ");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*teamId is required*");
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

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add(new DispatchedCommand(actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        public sealed record DispatchedCommand(string ActorId, EventEnvelope Envelope);
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
                new Aevatar.CQRS.Core.Commands.DefaultCommandContextPolicy(),
                new StudioProjectionActorCommandEnvelopeFactory(),
                new Aevatar.CQRS.Core.Commands.ActorCommandTargetDispatcher<StudioProjectionActorCommandTarget>(dispatchPort),
                new StudioProjectionActorCommandReceiptFactory()));
        return new StudioProjectionActorCommandDispatch(service);
    }
}
