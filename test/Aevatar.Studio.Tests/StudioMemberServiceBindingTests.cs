using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Locks in the most important invariants from issue #325:
///
///   - Member binding never falls back to the scope default service.
///   - The ServiceId Studio sends to the underlying scope binding command is
///     the member's stable publishedServiceId, sourced from the authority
///     state — not derived from a frontend-supplied value.
///   - Renaming a member does not change publishedServiceId.
///   - workflow / script / gagent each route through the same orchestration.
///   - The resulting revision is recorded back on the member authority.
/// </summary>
public sealed class StudioMemberServiceBindingTests
{
    private const string ScopeId = "scope-1";
    private const string MemberId = "m-bind-test";
    private const string PublishedServiceId = "member-m-bind-test";

    [Fact]
    public async Task BindAsync_Workflow_ShouldUseMemberPublishedServiceId()
    {
        var detail = NewDetail(MemberImplementationKindNames.Workflow);
        var queryPort = new InMemoryQueryPort(detail);
        var commandPort = new RecordingCommandPort();
        var bindingPort = new RecordingScopeBindingPort();

        var service = NewService(commandPort, queryPort, bindingPort);

        var response = await service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(["workflow:\n  name: x"])),
            CancellationToken.None);

        // The bind orchestration MUST hand the platform binding port the
        // member's stable publishedServiceId — never null/empty (which would
        // fall back to the scope default service).
        bindingPort.LastRequest.Should().NotBeNull();
        bindingPort.LastRequest!.ServiceId.Should().Be(PublishedServiceId);
        bindingPort.LastRequest.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Workflow);
        bindingPort.LastRequest.Workflow!.WorkflowYamls.Should().ContainSingle();

        // Lifecycle fix: BindAsync must persist the resolved impl_ref on the
        // member (UpdateImplementationAsync) BEFORE recording the binding
        // (RecordBindingAsync), so the actor walks Created → BuildReady →
        // BindReady on every bind. Both must run, and impl_update must
        // happen first.
        commandPort.OperationsInOrder.Should().Equal(
            "UpdateImplementation", "RecordBinding");
        commandPort.RecordedImplementationUpdates.Should().ContainSingle()
            .Which.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);

        // The orchestrator records the resulting revision back on the
        // member authority so /members/.../binding can read it from the
        // read model later.
        commandPort.RecordedBindings.Should().ContainSingle()
            .Which.PublishedServiceId.Should().Be(PublishedServiceId);
        response.PublishedServiceId.Should().Be(PublishedServiceId);
        response.RevisionId.Should().Be(bindingPort.IssuedRevisionId);
        response.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
    }

    [Fact]
    public async Task BindAsync_Script_ShouldRouteThroughScriptingKind()
    {
        var detail = NewDetail(MemberImplementationKindNames.Script);
        var queryPort = new InMemoryQueryPort(detail);
        var commandPort = new RecordingCommandPort();
        var bindingPort = new RecordingScopeBindingPort();

        var service = NewService(commandPort, queryPort, bindingPort);

        await service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Script: new StudioMemberScriptBindingSpec(ScriptId: "s-1", ScriptRevision: "v3")),
            CancellationToken.None);

        bindingPort.LastRequest!.ServiceId.Should().Be(PublishedServiceId);
        bindingPort.LastRequest.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Scripting);
        bindingPort.LastRequest.Script!.ScriptId.Should().Be("s-1");
        bindingPort.LastRequest.Script.ScriptRevision.Should().Be("v3");

        commandPort.OperationsInOrder.Should().Equal(
            "UpdateImplementation", "RecordBinding");
        commandPort.RecordedImplementationUpdates.Should().ContainSingle()
            .Which.ScriptId.Should().Be("s-1");
    }

    [Fact]
    public async Task BindAsync_GAgent_ShouldRouteThroughGAgentKind()
    {
        var detail = NewDetail(MemberImplementationKindNames.GAgent);
        var queryPort = new InMemoryQueryPort(detail);
        var commandPort = new RecordingCommandPort();
        var bindingPort = new RecordingScopeBindingPort();

        var service = NewService(commandPort, queryPort, bindingPort);

        await service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                GAgent: new StudioMemberGAgentBindingSpec(
                    ActorTypeName: "MyActor",
                    Endpoints: [
                        new StudioMemberGAgentEndpointSpec(
                            EndpointId: "chat",
                            DisplayName: "Chat",
                            Kind: "chat",
                            RequestTypeUrl: "type.googleapis.com/x.Request",
                            ResponseTypeUrl: "type.googleapis.com/x.Response")
                    ])),
            CancellationToken.None);

        bindingPort.LastRequest!.ServiceId.Should().Be(PublishedServiceId);
        bindingPort.LastRequest.ImplementationKind.Should().Be(ScopeBindingImplementationKind.GAgent);
        bindingPort.LastRequest.GAgent!.ActorTypeName.Should().Be("MyActor");
        bindingPort.LastRequest.GAgent.Endpoints.Should().ContainSingle();
        bindingPort.LastRequest.GAgent.Endpoints[0].Kind.Should().Be(ServiceEndpointKind.Chat);

        commandPort.OperationsInOrder.Should().Equal(
            "UpdateImplementation", "RecordBinding");
        commandPort.RecordedImplementationUpdates.Should().ContainSingle()
            .Which.ActorTypeName.Should().Be("MyActor");
    }

    [Fact]
    public async Task BindAsync_ShouldFail_WhenMemberDoesNotExist()
    {
        var queryPort = new InMemoryQueryPort(detail: null);
        var service = NewService(
            new RecordingCommandPort(),
            queryPort,
            new RecordingScopeBindingPort());

        var act = () => service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(["workflow:"])),
            CancellationToken.None);

        // Assert the typed exception so a regression that swaps it for a
        // plain InvalidOperationException is caught — endpoints map the
        // typed one to 404 and untyped IOEx to 400.
        await act.Should().ThrowAsync<StudioMemberNotFoundException>()
            .WithMessage("*not found in scope*");
    }

    [Fact]
    public async Task BindAsync_ShouldFail_WhenWorkflowYamlsAreMissing()
    {
        var detail = NewDetail(MemberImplementationKindNames.Workflow);
        var service = NewService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(detail),
            new RecordingScopeBindingPort());

        var act = () => service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*workflow yamls are required*");
    }

    [Fact]
    public async Task GetBindingAsync_ShouldReturnLastRecordedBinding()
    {
        var detail = NewDetail(MemberImplementationKindNames.Workflow);
        var withBinding = detail with
        {
            LastBinding = new StudioMemberBindingContractResponse(
                PublishedServiceId: PublishedServiceId,
                RevisionId: "rev-9",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                BoundAt: DateTimeOffset.UtcNow),
        };

        var service = NewService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(withBinding),
            new RecordingScopeBindingPort());

        var binding = await service.GetBindingAsync(ScopeId, MemberId);

        binding.Should().NotBeNull();
        binding!.PublishedServiceId.Should().Be(PublishedServiceId);
        binding.RevisionId.Should().Be("rev-9");
    }

    // Bind / GetBinding don't touch the lifecycle/command ports. We pass
    // throwing stubs so that any future regression which routes a bind
    // through the platform service ports — instead of through the existing
    // IScopeBindingCommandPort — fails loudly here rather than silently
    // green.
    private static StudioMemberService NewService(
        IStudioMemberCommandPort memberCommandPort,
        IStudioMemberQueryPort memberQueryPort,
        IScopeBindingCommandPort scopeBindingCommandPort) =>
        new(
            memberCommandPort,
            memberQueryPort,
            scopeBindingCommandPort,
            new ThrowingServiceLifecycleQueryPort(),
            new ThrowingServiceCommandPort());

    private static StudioMemberDetailResponse NewDetail(string implementationKindWire)
    {
        var summary = new StudioMemberSummaryResponse(
            MemberId: MemberId,
            ScopeId: ScopeId,
            DisplayName: "Test Member",
            Description: string.Empty,
            ImplementationKind: implementationKindWire,
            LifecycleStage: MemberLifecycleStageNames.BuildReady,
            PublishedServiceId: PublishedServiceId,
            LastBoundRevisionId: null,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt: DateTimeOffset.UtcNow.AddHours(-1));

        return new StudioMemberDetailResponse(
            Summary: summary,
            ImplementationRef: null,
            LastBinding: null);
    }

    private sealed class InMemoryQueryPort : IStudioMemberQueryPort
    {
        private readonly StudioMemberDetailResponse? _detail;

        public InMemoryQueryPort(StudioMemberDetailResponse? detail)
        {
            _detail = detail;
        }

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default)
        {
            return Task.FromResult(new StudioMemberRosterResponse(
                ScopeId: scopeId,
                Members: _detail == null ? [] : [_detail.Summary]));
        }

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default)
        {
            return Task.FromResult(_detail);
        }
    }

    private sealed class RecordingCommandPort : IStudioMemberCommandPort
    {
        public List<RecordedBinding> RecordedBindings { get; } = [];

        public List<StudioMemberImplementationRefResponse> RecordedImplementationUpdates { get; } = [];

        public List<string> OperationsInOrder { get; } = [];

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId, CreateStudioMemberRequest request, CancellationToken ct = default)
        {
            throw new NotImplementedException("Not exercised in this test.");
        }

        public Task UpdateImplementationAsync(
            string scopeId,
            string memberId,
            StudioMemberImplementationRefResponse implementation,
            CancellationToken ct = default)
        {
            RecordedImplementationUpdates.Add(implementation);
            OperationsInOrder.Add("UpdateImplementation");
            return Task.CompletedTask;
        }

        public Task RecordBindingAsync(
            string scopeId,
            string memberId,
            string publishedServiceId,
            string revisionId,
            string implementationKindName,
            CancellationToken ct = default)
        {
            RecordedBindings.Add(new RecordedBinding(
                scopeId, memberId, publishedServiceId, revisionId, implementationKindName));
            OperationsInOrder.Add("RecordBinding");
            return Task.CompletedTask;
        }

        public Task ReassignTeamAsync(
            string scopeId, string memberId, string? fromTeamId, string? toTeamId,
            CancellationToken ct = default)
        {
            OperationsInOrder.Add("ReassignTeam");
            return Task.CompletedTask;
        }

        public sealed record RecordedBinding(
            string ScopeId,
            string MemberId,
            string PublishedServiceId,
            string RevisionId,
            string ImplementationKindName);
    }

    private sealed class ThrowingServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        public Task<ServiceCatalogSnapshot?> GetServiceAsync(
            ServiceIdentity identity, CancellationToken ct = default) =>
            throw new InvalidOperationException("bind orchestration must not query the platform lifecycle port.");

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            throw new InvalidOperationException("bind orchestration must not list services on the platform lifecycle port.");

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(
            ServiceIdentity identity, CancellationToken ct = default) =>
            throw new InvalidOperationException("bind orchestration must not read revisions through the platform lifecycle port.");

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(
            ServiceIdentity identity, CancellationToken ct = default) =>
            throw new InvalidOperationException("bind orchestration must not read deployments through the platform lifecycle port.");
    }

    private sealed class ThrowingServiceCommandPort : IServiceCommandPort
    {
        private static InvalidOperationException Reject(string method) =>
            new($"bind orchestration must not call IServiceCommandPort.{method} — that surface belongs to revision lifecycle, not bind.");

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(
            CreateServiceDefinitionCommand command, CancellationToken ct = default) => throw Reject(nameof(CreateServiceAsync));
        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(
            UpdateServiceDefinitionCommand command, CancellationToken ct = default) => throw Reject(nameof(UpdateServiceAsync));
        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(
            CreateServiceRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(CreateRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(
            PrepareServiceRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(PrepareRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(
            PublishServiceRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(PublishRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(
            RetireServiceRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(RetireRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(
            SetDefaultServingRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(SetDefaultServingRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(
            ActivateServiceRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(ActivateServiceRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(
            DeactivateServiceDeploymentCommand command, CancellationToken ct = default) => throw Reject(nameof(DeactivateServiceDeploymentAsync));
        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(
            ReplaceServiceServingTargetsCommand command, CancellationToken ct = default) => throw Reject(nameof(ReplaceServiceServingTargetsAsync));
        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(
            StartServiceRolloutCommand command, CancellationToken ct = default) => throw Reject(nameof(StartServiceRolloutAsync));
        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(
            AdvanceServiceRolloutCommand command, CancellationToken ct = default) => throw Reject(nameof(AdvanceServiceRolloutAsync));
        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(
            PauseServiceRolloutCommand command, CancellationToken ct = default) => throw Reject(nameof(PauseServiceRolloutAsync));
        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(
            ResumeServiceRolloutCommand command, CancellationToken ct = default) => throw Reject(nameof(ResumeServiceRolloutAsync));
        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(
            RollbackServiceRolloutCommand command, CancellationToken ct = default) => throw Reject(nameof(RollbackServiceRolloutAsync));
    }

    private sealed class RecordingScopeBindingPort : IScopeBindingCommandPort
    {
        public string IssuedRevisionId { get; } = "rev-test";

        public ScopeBindingUpsertRequest? LastRequest { get; private set; }

        public Task<ScopeBindingUpsertResult> UpsertAsync(
            ScopeBindingUpsertRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            // Mirror the production binding ports: populate the kind-specific
            // result so BindAsync can derive the resolved implementation_ref
            // and call UpdateImplementationAsync. Leaving these null skips
            // the lifecycle wiring entirely and silently passes any test
            // that doesn't assert on the call ordering.
            ScopeBindingWorkflowResult? workflowResult = null;
            ScopeBindingScriptResult? scriptResult = null;
            ScopeBindingGAgentResult? gagentResult = null;
            switch (request.ImplementationKind)
            {
                case ScopeBindingImplementationKind.Workflow:
                    workflowResult = new ScopeBindingWorkflowResult(
                        WorkflowName: $"wf-{request.ServiceId}",
                        DefinitionActorIdPrefix: $"def-{request.ServiceId}");
                    break;
                case ScopeBindingImplementationKind.Scripting:
                    scriptResult = new ScopeBindingScriptResult(
                        ScriptId: request.Script?.ScriptId ?? string.Empty,
                        ScriptRevision: request.Script?.ScriptRevision ?? IssuedRevisionId,
                        DefinitionActorId: $"def-{request.ServiceId}");
                    break;
                case ScopeBindingImplementationKind.GAgent:
                    gagentResult = new ScopeBindingGAgentResult(
                        ActorTypeName: request.GAgent?.ActorTypeName ?? string.Empty);
                    break;
            }

            return Task.FromResult(new ScopeBindingUpsertResult(
                ScopeId: request.ScopeId,
                ServiceId: request.ServiceId ?? string.Empty,
                DisplayName: request.DisplayName ?? string.Empty,
                RevisionId: IssuedRevisionId,
                ImplementationKind: request.ImplementationKind,
                ExpectedActorId: $"actor-{request.ServiceId}-{IssuedRevisionId}",
                Workflow: workflowResult,
                Script: scriptResult,
                GAgent: gagentResult));
        }
    }
}
