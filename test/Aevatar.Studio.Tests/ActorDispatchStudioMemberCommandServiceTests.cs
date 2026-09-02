using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Locks in the write-side invariants for the StudioMember command service:
///
/// - CreateAsync routes through the canonical actor id and seeds the
///   immutable publishedServiceId from the member id (rename-safe).
/// - CreateAsync is shell-only; implementation_ref enters through
///   UpdateImplementationAsync / binding, not through the created event.
/// - Binding requests route through the run actor with a stable payload hash.
/// - Dispatch always goes through IStudioActorBootstrap before
///   IActorDispatchPort, so actor provisioning happens before the command
///   lands on the inbox.
/// </summary>
public sealed class ActorDispatchStudioMemberCommandServiceTests
{
    private const string ScopeId = "scope-1";

    [Fact]
    public async Task CreateAsync_ShouldDispatchCreatedEventToCanonicalActor()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, CreateCommandDispatch(dispatch));

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
        evt.ImplementationRef.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ShouldGenerateMemberId_WhenRequestOmitsIt()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, CreateCommandDispatch(dispatch));

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

    [Fact]
    public async Task CreateAsync_ShouldRejectImplementationRefBeforeDispatch()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(
            bootstrap,
            CreateCommandDispatch(dispatch));

        var act = () => service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                MemberId: "m-alpha",
                ImplementationRef: new StudioMemberImplementationRefResponse(
                    ImplementationKind: MemberImplementationKindNames.Workflow,
                    WorkflowId: "wf-alpha",
                    WorkflowRevision: "rev-1")),
            CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<StudioMemberCreateImplementationRefNotAllowedException>();
        thrown.Which.ScopeId.Should().Be(ScopeId);
        thrown.Which.Field.Should().Be("implementationRef");
        bootstrap.EnsuredActorIds.Should().BeEmpty();
        dispatch.Dispatches.Should().BeEmpty();
    }

    // Note: input validation (length caps, slug pattern, empty display
    // name) is now enforced at the Application boundary in
    // StudioMemberCreateRequestValidator. The Projection-layer command
    // service is intentionally lenient and trusts already-validated input.
    // Validator-level coverage lives in StudioMemberCreateRequestValidatorTests.

    [Fact]
    public async Task CreateAsync_ShouldRejectUnknownImplementationKind()
    {
        var service = new ActorDispatchStudioMemberCommandService(new RecordingBootstrap(), CreateCommandDispatch(new RecordingDispatchPort()));

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
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, CreateCommandDispatch(dispatch));

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
                DiagnosticActorTypeName: "MyActor"),
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
    public async Task RecordPublishedBindingAsync_ShouldDispatchPublishedBindingRecordedEvent()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, CreateCommandDispatch(dispatch));

        await service.RecordPublishedBindingAsync(
            ScopeId,
            "m-1",
            new StudioMemberPublishedBindingRecordRequest(
                PublishedServiceId: "member-m-1",
                RevisionId: "rev-updated",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                ImplementationRef: new StudioMemberImplementationRefResponse(
                    MemberImplementationKindNames.Workflow,
                    WorkflowId: "workflow-1",
                    WorkflowRevision: "rev-updated"),
                ExpectedActorId: "workflow-definition:workflow-1"),
            CancellationToken.None);

        bootstrap.EnsuredActorIds.Should().ContainSingle()
            .Which.Should().Be("studio-member:scope-1:m-1");
        dispatch.Dispatches.Should().ContainSingle();
        var dispatched = dispatch.Dispatches[0];
        dispatched.ActorId.Should().Be("studio-member:scope-1:m-1");
        dispatched.Envelope.Payload.Is(StudioMemberPublishedBindingRecordedEvent.Descriptor).Should().BeTrue();
        var evt = dispatched.Envelope.Payload.Unpack<StudioMemberPublishedBindingRecordedEvent>();
        evt.PublishedServiceId.Should().Be("member-m-1");
        evt.RevisionId.Should().Be("rev-updated");
        evt.ImplementationKind.Should().Be(StudioMemberImplementationKind.Workflow);
        evt.ImplementationRef.Workflow.WorkflowId.Should().Be("workflow-1");
        evt.ImplementationRef.Workflow.WorkflowRevision.Should().Be("rev-updated");
        evt.ExpectedActorId.Should().Be("workflow-definition:workflow-1");
        evt.RecordedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task RenameAsync_ShouldDispatchRenamedEventToCanonicalActor()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, CreateCommandDispatch(dispatch));

        await service.RenameAsync(ScopeId, "m-1", "  Renamed Workflow  ", CancellationToken.None);

        bootstrap.EnsuredActorIds.Should().ContainSingle()
            .Which.Should().Be("studio-member:scope-1:m-1");
        dispatch.Dispatches.Should().ContainSingle();
        var dispatched = dispatch.Dispatches[0];
        dispatched.ActorId.Should().Be("studio-member:scope-1:m-1");
        dispatched.Envelope.Payload.Is(StudioMemberRenamedEvent.Descriptor).Should().BeTrue();
        var evt = dispatched.Envelope.Payload.Unpack<StudioMemberRenamedEvent>();
        evt.DisplayName.Should().Be("Renamed Workflow");
        evt.UpdatedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldDispatchTypedDeleteRequestToCanonicalActor()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, CreateCommandDispatch(dispatch));

        await service.DeleteAsync(ScopeId, "m-1", CancellationToken.None);

        bootstrap.EnsuredActorIds.Should().ContainSingle()
            .Which.Should().Be("studio-member:scope-1:m-1");
        dispatch.Dispatches.Should().ContainSingle();
        var dispatched = dispatch.Dispatches[0];
        dispatched.ActorId.Should().Be("studio-member:scope-1:m-1");
        dispatched.Envelope.Payload.Is(StudioMemberDeleteRequested.Descriptor).Should().BeTrue();
        var evt = dispatched.Envelope.Payload.Unpack<StudioMemberDeleteRequested>();
        evt.ScopeId.Should().Be(ScopeId);
        evt.MemberId.Should().Be("m-1");
        evt.RequestedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task StartBindingRunAsync_ShouldDispatchRequestedEventToRunActor()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, CreateCommandDispatch(dispatch));

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
        evt.Request.RequestHash.Should().NotBeNullOrWhiteSpace();
        evt.Request.RequestHash.Should().MatchRegex("^[0-9a-f]{64}$");
        evt.Request.Script.ScriptId.Should().Be("script-1");
        evt.Request.Script.ScriptRevision.Should().Be("rev-a");
    }

    [Fact]
    public async Task StartBindingRunAsync_ShouldDispatchWorkflowBindingPayload()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, CreateCommandDispatch(dispatch));
        var admissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "workflow:\n  name: alpha_runtime",
            new Dictionary<string, string>
            {
                ["beta"] = "workflow:\n  name: beta",
            },
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);

        await service.StartBindingRunAsync(
            new StudioMemberBindingRunStartRequest(
                BindingRunId: "bind-workflow",
                ScopeId: ScopeId,
                MemberId: "m-1",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                Binding: new UpdateStudioMemberBindingRequest(
                    RevisionId: "rev-explicit",
                    Workflow: new StudioMemberWorkflowBindingSpec(
                        "workflow-stable-id",
                        [
                            "workflow:\n  name: alpha_runtime",
                            "workflow:\n  name: beta",
                        ])
                    {
                        CapabilityAdmissionPlan = admissionPlan,
                    })),
            CancellationToken.None);

        var evt = dispatch.Dispatches.Should().ContainSingle().Which
            .Envelope.Payload.Unpack<StudioMemberBindingRunRequested>();
        evt.Request.RevisionId.Should().Be("rev-explicit");
        evt.Request.Workflow.WorkflowId.Should().Be("workflow-stable-id");
        evt.Request.Workflow.WorkflowYamls.Should().Equal(
            "workflow:\n  name: alpha_runtime",
            "workflow:\n  name: beta");
        evt.Request.Workflow.CapabilityAdmissionPlan.AdmissionDigest.Should()
            .Be(admissionPlan.AdmissionDigest);
    }

    [Fact]
    public async Task StartBindingRunAsync_ShouldComputeStableHashFromPayload()
    {
        var firstDispatch = new RecordingDispatchPort();
        var firstService = new ActorDispatchStudioMemberCommandService(new RecordingBootstrap(), CreateCommandDispatch(firstDispatch));
        await firstService.StartBindingRunAsync(NewScriptRunStartRequest("bind-1", "rev-a"), CancellationToken.None);

        var repeatDispatch = new RecordingDispatchPort();
        var repeatService = new ActorDispatchStudioMemberCommandService(new RecordingBootstrap(), CreateCommandDispatch(repeatDispatch));
        await repeatService.StartBindingRunAsync(NewScriptRunStartRequest("bind-1", "rev-a"), CancellationToken.None);

        var changedDispatch = new RecordingDispatchPort();
        var changedService = new ActorDispatchStudioMemberCommandService(new RecordingBootstrap(), CreateCommandDispatch(changedDispatch));
        await changedService.StartBindingRunAsync(NewScriptRunStartRequest("bind-1", "rev-b"), CancellationToken.None);

        var firstHash = firstDispatch.Dispatches[0].Envelope.Payload
            .Unpack<StudioMemberBindingRunRequested>().Request.RequestHash;
        var repeatHash = repeatDispatch.Dispatches[0].Envelope.Payload
            .Unpack<StudioMemberBindingRunRequested>().Request.RequestHash;
        var changedHash = changedDispatch.Dispatches[0].Envelope.Payload
            .Unpack<StudioMemberBindingRunRequested>().Request.RequestHash;

        firstHash.Should().MatchRegex("^[0-9a-f]{64}$");
        repeatHash.Should().Be(firstHash);
        changedHash.Should().NotBe(firstHash);
    }

    [Fact]
    public async Task StartBindingRunAsync_ShouldDispatchGAgentBindingPayload()
    {
        var bootstrap = new RecordingBootstrap();
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(bootstrap, CreateCommandDispatch(dispatch));

        await service.StartBindingRunAsync(
            new StudioMemberBindingRunStartRequest(
                BindingRunId: "bind-gagent",
                ScopeId: ScopeId,
                MemberId: "m-1",
                ImplementationKind: MemberImplementationKindNames.GAgent,
                Binding: new UpdateStudioMemberBindingRequest(
                    GAgent: new StudioMemberGAgentBindingSpec(
                        AgentKind: "my-company.my-gagent",
                        Endpoints: [
                            new StudioMemberGAgentEndpointSpec(
                                EndpointId: "chat",
                                DisplayName: "Chat",
                                Kind: "chat",
                                RequestTypeUrl: "type.googleapis.com/a.Request",
                                ResponseTypeUrl: "type.googleapis.com/a.Response",
                                Description: "chat endpoint")
                        ]))),
            CancellationToken.None);

        var evt = dispatch.Dispatches.Should().ContainSingle().Which
            .Envelope.Payload.Unpack<StudioMemberBindingRunRequested>();
        evt.Request.Gagent.AgentKind.Should().Be("my-company.my-gagent");
        evt.Request.Gagent.Endpoints.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new StudioMemberGAgentEndpointBindingRequest
            {
                EndpointId = "chat",
                DisplayName = "Chat",
                Kind = StudioMemberGAgentEndpointKind.Chat,
                RequestTypeUrl = "type.googleapis.com/a.Request",
                ResponseTypeUrl = "type.googleapis.com/a.Response",
                Description = "chat endpoint",
            });
    }

    [Fact]
    public async Task StartBindingRunAsync_ShouldDefaultMissingGAgentEndpointResponseTypeUrl()
    {
        var dispatch = new RecordingDispatchPort();
        var service = new ActorDispatchStudioMemberCommandService(new RecordingBootstrap(), CreateCommandDispatch(dispatch));

        await service.StartBindingRunAsync(
            new StudioMemberBindingRunStartRequest(
                BindingRunId: "bind-gagent",
                ScopeId: ScopeId,
                MemberId: "m-1",
                ImplementationKind: MemberImplementationKindNames.GAgent,
                Binding: new UpdateStudioMemberBindingRequest(
                    GAgent: new StudioMemberGAgentBindingSpec(
                        AgentKind: "example.studio.command-member",
                        Endpoints: [
                            new StudioMemberGAgentEndpointSpec(
                                EndpointId: "run",
                                DisplayName: "Run",
                                Kind: "command",
                                RequestTypeUrl: "type.googleapis.com/google.protobuf.StringValue",
                                ResponseTypeUrl: null!)
                            {
                                Description = "You are the team member gagent."
                            }
                        ]))),
            CancellationToken.None);

        var endpoint = dispatch.Dispatches.Should().ContainSingle().Which
            .Envelope.Payload.Unpack<StudioMemberBindingRunRequested>()
            .Request.Gagent.Endpoints.Should().ContainSingle().Which;
        endpoint.EndpointId.Should().Be("run");
        endpoint.Kind.Should().Be(StudioMemberGAgentEndpointKind.Command);
        endpoint.RequestTypeUrl.Should().Be("type.googleapis.com/google.protobuf.StringValue");
        endpoint.ResponseTypeUrl.Should().BeEmpty();
        endpoint.Description.Should().Be("You are the team member gagent.");
    }

    [Fact]
    public async Task StartBindingRunAsync_ShouldRejectMissingGAgentEndpointKind()
    {
        var service = new ActorDispatchStudioMemberCommandService(new RecordingBootstrap(), CreateCommandDispatch(new RecordingDispatchPort()));

        var act = () => service.StartBindingRunAsync(
            new StudioMemberBindingRunStartRequest(
                BindingRunId: "bind-gagent",
                ScopeId: ScopeId,
                MemberId: "m-1",
                ImplementationKind: MemberImplementationKindNames.GAgent,
                Binding: new UpdateStudioMemberBindingRequest(
                    GAgent: new StudioMemberGAgentBindingSpec(
                        AgentKind: "my-company.my-gagent",
                        Endpoints: [
                            new StudioMemberGAgentEndpointSpec(
                                EndpointId: "chat",
                                DisplayName: "Chat",
                                Kind: "",
                                RequestTypeUrl: "type.googleapis.com/a.Request",
                                ResponseTypeUrl: "type.googleapis.com/a.Response")
                        ]))),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*gagent endpoint kind is required*");
    }

    [Fact]
    public void Constructor_ShouldRejectNullDependencies()
    {
        FluentActions.Invoking(() =>
                new ActorDispatchStudioMemberCommandService(null!, CreateCommandDispatch(new RecordingDispatchPort())))
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

    private static StudioMemberBindingRunStartRequest NewScriptRunStartRequest(
        string bindingRunId,
        string scriptRevision) =>
        new(
            BindingRunId: bindingRunId,
            ScopeId: ScopeId,
            MemberId: "m-1",
            ImplementationKind: MemberImplementationKindNames.Script,
            Binding: new UpdateStudioMemberBindingRequest(
                Script: new StudioMemberScriptBindingSpec(
                    ScriptId: "script-1",
                    ScriptRevision: scriptRevision)));
}
