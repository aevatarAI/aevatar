using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.CommandServices;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Locks in the write-side invariants for the StudioMember command service:
///
/// - CreateAsync routes through the canonical actor id and seeds the
///   immutable publishedServiceId from the member id (rename-safe).
/// - All three implementation kinds (workflow / script / gagent) build the
///   typed implementation_ref the actor expects.
/// - RecordBindingAsync rejects empty publishedServiceId / revisionId so the
///   member authority cannot record a degenerate binding.
/// - Dispatch always goes through IStudioActorBootstrap before
///   IActorDispatchPort, so the projection scope is active before the
///   command lands on the inbox.
/// </summary>
public sealed class ActorDispatchStudioMemberCommandServiceTests
{
    private const string ScopeId = "scope-1";

    [Fact]
    public async Task CreateAsync_ShouldDispatchCreatedEventToCanonicalActor()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, dispatch);

        var summary = await service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                Description: "first member",
                MemberId: "m-alpha"),
            CancellationToken.None);

        summary.MemberId.Should().Be("m-alpha");
        summary.ScopeId.Should().Be(ScopeId);
        summary.PublishedServiceId.Should().Be("member-m-alpha");
        summary.LifecycleStage.Should().Be(MemberLifecycleStageNames.Created);
        summary.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);

        bootstrap.EnsuredActorIds.Should().ContainSingle()
            .Which.Should().Be("studio-member:scope-1:m-alpha");
        dispatch.Dispatches.Should().ContainSingle();

        var dispatched = dispatch.Dispatches[0];
        dispatched.ActorId.Should().Be("studio-member:scope-1:m-alpha");
        dispatched.Envelope.Payload.Is(StudioMemberCreatedEvent.Descriptor).Should().BeTrue();
        var evt = dispatched.Envelope.Payload.Unpack<StudioMemberCreatedEvent>();
        evt.MemberId.Should().Be("m-alpha");
        evt.PublishedServiceId.Should().Be("member-m-alpha");
        evt.DisplayName.Should().Be("Alpha");
        evt.Description.Should().Be("first member");
    }

    [Fact]
    public async Task CreateAsync_ShouldGenerateMemberId_WhenRequestOmitsIt()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, dispatch);

        var summary = await service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Auto",
                ImplementationKind: MemberImplementationKindNames.Script),
            CancellationToken.None);

        summary.MemberId.Should().StartWith("m-");
        summary.PublishedServiceId.Should().Be($"member-{summary.MemberId}");
        summary.MemberId.Should().NotContain(":");
    }

    // Note: input validation (length caps, slug pattern, empty display
    // name) is now enforced at the Application boundary in
    // StudioMemberCreateRequestValidator. The Projection-layer command
    // service is intentionally lenient and trusts already-validated input.
    // Validator-level coverage lives in StudioMemberCreateRequestValidatorTests.

    [Fact]
    public async Task CreateAsync_ShouldRejectUnknownImplementationKind()
    {
        var service = new ActorDispatchStudioMemberCommandService(
            new RecordingBootstrap(),
            new RecordingDispatchPort());

        var act = () => service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Test",
                ImplementationKind: "weird"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unknown implementationKind*");
    }

    [Theory]
    [InlineData(MemberImplementationKindNames.Workflow)]
    [InlineData(MemberImplementationKindNames.Script)]
    [InlineData(MemberImplementationKindNames.GAgent)]
    public async Task UpdateImplementationAsync_ShouldDispatchTypedRefForEachKind(string kind)
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, dispatch);

        var implementation = kind switch
        {
            MemberImplementationKindNames.Workflow => new StudioMemberImplementationRefResponse(
                ImplementationKind: kind,
                WorkflowId: "wf-1",
                WorkflowRevision: "v1"),
            MemberImplementationKindNames.Script => new StudioMemberImplementationRefResponse(
                ImplementationKind: kind,
                ScriptId: "s-1",
                ScriptRevision: "v2"),
            MemberImplementationKindNames.GAgent => new StudioMemberImplementationRefResponse(
                ImplementationKind: kind,
                ActorTypeName: "MyActor"),
            _ => throw new InvalidOperationException("unreachable"),
        };

        await service.UpdateImplementationAsync(ScopeId, "m-1", implementation, CancellationToken.None);

        dispatch.Dispatches.Should().ContainSingle();
        var evt = dispatch.Dispatches[0].Envelope.Payload.Unpack<StudioMemberImplementationUpdatedEvent>();
        switch (kind)
        {
            case MemberImplementationKindNames.Workflow:
                evt.ImplementationKind.Should().Be(StudioMemberImplementationKind.Workflow);
                evt.ImplementationRef.Workflow.WorkflowId.Should().Be("wf-1");
                evt.ImplementationRef.Workflow.WorkflowRevision.Should().Be("v1");
                break;
            case MemberImplementationKindNames.Script:
                evt.ImplementationKind.Should().Be(StudioMemberImplementationKind.Script);
                evt.ImplementationRef.Script.ScriptId.Should().Be("s-1");
                evt.ImplementationRef.Script.ScriptRevision.Should().Be("v2");
                break;
            case MemberImplementationKindNames.GAgent:
                evt.ImplementationKind.Should().Be(StudioMemberImplementationKind.Gagent);
                evt.ImplementationRef.Gagent.ActorTypeName.Should().Be("MyActor");
                break;
        }
    }

    [Fact]
    public async Task RecordBindingAsync_ShouldRejectEmptyPublishedServiceId()
    {
        var service = new ActorDispatchStudioMemberCommandService(
            new RecordingBootstrap(), new RecordingDispatchPort());

        var act = () => service.RecordBindingAsync(
            ScopeId, "m-1", "", "rev-1", MemberImplementationKindNames.Workflow, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*publishedServiceId is required*");
    }

    [Fact]
    public async Task RecordBindingAsync_ShouldRejectEmptyRevisionId()
    {
        var service = new ActorDispatchStudioMemberCommandService(
            new RecordingBootstrap(), new RecordingDispatchPort());

        var act = () => service.RecordBindingAsync(
            ScopeId, "m-1", "member-m-1", "", MemberImplementationKindNames.Workflow, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*revisionId is required*");
    }

    [Fact]
    public async Task RecordBindingAsync_ShouldDispatchBoundEvent()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, dispatch);

        await service.RecordBindingAsync(
            ScopeId,
            "m-1",
            "member-m-1",
            "rev-7",
            MemberImplementationKindNames.GAgent,
            CancellationToken.None);

        bootstrap.EnsuredActorIds.Should().ContainSingle()
            .Which.Should().Be("studio-member:scope-1:m-1");
        dispatch.Dispatches.Should().ContainSingle();
        var evt = dispatch.Dispatches[0].Envelope.Payload.Unpack<StudioMemberBindingCompletedEvent>();
        evt.PublishedServiceId.Should().Be("member-m-1");
        evt.RevisionId.Should().Be("rev-7");
        evt.ImplementationKind.Should().Be(StudioMemberImplementationKind.Gagent);
        evt.BindingRunId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task StartBindingRunAsync_ShouldDispatchRequestedEventToRunActor()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, dispatch);

        await service.StartBindingRunAsync(
            new StudioMemberBindingRunStartRequest(
                BindingRunId: "bind-1",
                ScopeId: ScopeId,
                MemberId: "m-1",
                ImplementationKind: MemberImplementationKindNames.Script,
                Binding: new UpdateStudioMemberBindingRequest(
                    Script: new StudioMemberScriptBindingSpec(
                        ScriptId: "script-1",
                        ScriptRevision: "rev-a"))),
            CancellationToken.None);

        bootstrap.EnsuredActorIds.Should().Equal(
            "studio-member-binding-run:bind-1",
            "studio-member:scope-1:m-1");
        dispatch.Dispatches.Should().ContainSingle();
        var dispatched = dispatch.Dispatches[0];
        dispatched.ActorId.Should().Be("studio-member-binding-run:bind-1");
        dispatched.Envelope.Payload.Is(StudioMemberBindingRunRequested.Descriptor).Should().BeTrue();
        var evt = dispatched.Envelope.Payload.Unpack<StudioMemberBindingRunRequested>();
        evt.Request.BindingRunId.Should().Be("bind-1");
        evt.Request.ScopeId.Should().Be(ScopeId);
        evt.Request.MemberId.Should().Be("m-1");
        evt.Request.Script.ScriptId.Should().Be("script-1");
        evt.Request.Script.ScriptRevision.Should().Be("rev-a");
    }

    [Fact]
    public void Constructor_ShouldRejectNullDependencies()
    {
        FluentActions.Invoking(() =>
                new ActorDispatchStudioMemberCommandService(null!, new RecordingDispatchPort()))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() =>
                new ActorDispatchStudioMemberCommandService(new RecordingBootstrap(), null!))
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
