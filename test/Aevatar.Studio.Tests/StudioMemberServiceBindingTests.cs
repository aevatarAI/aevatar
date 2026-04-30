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
///   - Bind admission is no longer read-model gated.
///   - Bind returns an honest async accepted receipt.
///   - workflow / script / gagent requests keep their typed payload shape.
/// </summary>
public sealed class StudioMemberServiceBindingTests
{
    private const string ScopeId = "scope-1";
    private const string MemberId = "m-bind-test";
    private const string PublishedServiceId = "member-m-bind-test";

    [Fact]
    public async Task BindAsync_Workflow_ShouldDispatchBindingRunWithoutReadingMember()
    {
        var queryPort = new ThrowingBindQueryPort();
        var commandPort = new RecordingCommandPort();

        var service = NewService(commandPort, queryPort);

        var response = await service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(["workflow:\n  name: x"])),
            CancellationToken.None);

        response.Status.Should().Be(StudioMemberBindingRunStatusNames.Accepted);
        response.BindingRunId.Should().StartWith("bind-");
        response.ScopeId.Should().Be(ScopeId);
        response.MemberId.Should().Be(MemberId);

        var started = commandPort.StartedRuns.Should().ContainSingle().Which;
        started.BindingRunId.Should().Be(response.BindingRunId);
        started.ScopeId.Should().Be(ScopeId);
        started.MemberId.Should().Be(MemberId);
        started.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
        started.Binding.Workflow!.WorkflowYamls.Should().ContainSingle();
    }

    [Fact]
    public async Task BindAsync_Script_ShouldRouteThroughScriptingKind()
    {
        var queryPort = new ThrowingBindQueryPort();
        var commandPort = new RecordingCommandPort();

        var service = NewService(commandPort, queryPort);

        await service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Script: new StudioMemberScriptBindingSpec(ScriptId: "s-1", ScriptRevision: "v3")),
            CancellationToken.None);

        var started = commandPort.StartedRuns.Should().ContainSingle().Which;
        started.ImplementationKind.Should().Be(MemberImplementationKindNames.Script);
        started.Binding.Script!.ScriptId.Should().Be("s-1");
        started.Binding.Script.ScriptRevision.Should().Be("v3");
    }

    [Fact]
    public async Task BindAsync_GAgent_ShouldRouteThroughGAgentKind()
    {
        var queryPort = new ThrowingBindQueryPort();
        var commandPort = new RecordingCommandPort();

        var service = NewService(commandPort, queryPort);

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

        var started = commandPort.StartedRuns.Should().ContainSingle().Which;
        started.ImplementationKind.Should().Be(MemberImplementationKindNames.GAgent);
        started.Binding.GAgent!.ActorTypeName.Should().Be("MyActor");
        started.Binding.GAgent.Endpoints.Should().ContainSingle()
            .Which.Kind.Should().Be("chat");
    }

    [Fact]
    public async Task BindAsync_ShouldAccept_WhenMemberReadModelDoesNotExistYet()
    {
        var commandPort = new RecordingCommandPort();
        var service = NewService(commandPort, new ThrowingBindQueryPort());

        var response = await service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(["workflow:"])),
            CancellationToken.None);

        response.Status.Should().Be(StudioMemberBindingRunStatusNames.Accepted);
        commandPort.StartedRuns.Should().ContainSingle();
    }

    [Fact]
    public async Task BindAsync_ShouldFail_WhenBindingImplementationIsMissing()
    {
        var service = NewService(
            new RecordingCommandPort(),
            new ThrowingBindQueryPort());

        var act = () => service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exactly one binding implementation is required*");
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
            new InMemoryQueryPort(withBinding));

        var binding = await service.GetBindingAsync(ScopeId, MemberId);

        binding.LastBinding.Should().NotBeNull();
        binding.LastBinding!.PublishedServiceId.Should().Be(PublishedServiceId);
        binding.LastBinding.RevisionId.Should().Be("rev-9");
    }

    [Fact]
    public async Task GetBindingRunAsync_ShouldReadBindingRunQueryPort()
    {
        var runQuery = new InMemoryBindingRunQueryPort(new StudioMemberBindingRunStatusResponse(
            BindingRunId: "bind-1",
            Status: StudioMemberBindingRunStatusNames.PlatformBindingPending,
            UpdatedAt: DateTimeOffset.UtcNow)
        {
            PlatformBindingCommandId = "platform-bind-1",
        });
        var service = NewService(
            new RecordingCommandPort(),
            new ThrowingBindQueryPort(),
            runQuery);

        var run = await service.GetBindingRunAsync(ScopeId, MemberId, "bind-1");

        run.BindingRunId.Should().Be("bind-1");
        run.PlatformBindingCommandId.Should().Be("platform-bind-1");
        runQuery.Requests.Should().ContainSingle().Which.Should().Be((ScopeId, MemberId, "bind-1"));
    }

    // Bind / GetBinding don't touch the lifecycle/command ports. We pass
    // throwing stubs so that any future regression which routes a bind
    // through the platform service ports — instead of through the existing
    // IScopeBindingCommandPort — fails loudly here rather than silently
    // green.
    private static StudioMemberService NewService(
        IStudioMemberCommandPort memberCommandPort,
        IStudioMemberQueryPort memberQueryPort,
        IStudioMemberBindingRunQueryPort? bindingRunQueryPort = null) =>
        new(
            memberCommandPort,
            memberQueryPort,
            bindingRunQueryPort ?? new InMemoryBindingRunQueryPort(null),
            new InertTeamQueryPort(),
            new ThrowingServiceLifecycleQueryPort(),
            new ThrowingServiceCommandPort());

    private sealed class InertTeamQueryPort : IStudioTeamQueryPort
    {
        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId, StudioTeamRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, []));

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId, string teamId, CancellationToken ct = default) =>
            Task.FromResult<StudioTeamSummaryResponse?>(null);
    }

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

    private sealed class ThrowingBindQueryPort : IStudioMemberQueryPort
    {
        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default)
        {
            throw new InvalidOperationException("BindAsync must not query StudioMember read models.");
        }

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default)
        {
            throw new InvalidOperationException("BindAsync must not query StudioMember read models.");
        }
    }

    private sealed class InMemoryBindingRunQueryPort : IStudioMemberBindingRunQueryPort
    {
        private readonly StudioMemberBindingRunStatusResponse? _run;

        public InMemoryBindingRunQueryPort(StudioMemberBindingRunStatusResponse? run)
        {
            _run = run;
        }

        public List<(string ScopeId, string MemberId, string BindingRunId)> Requests { get; } = [];

        public Task<StudioMemberBindingRunStatusResponse?> GetAsync(
            string scopeId,
            string memberId,
            string bindingRunId,
            CancellationToken ct = default)
        {
            Requests.Add((scopeId, memberId, bindingRunId));
            return Task.FromResult(_run);
        }
    }

    private sealed class RecordingCommandPort : IStudioMemberCommandPort
    {
        public List<RecordedBinding> RecordedBindings { get; } = [];

        public List<StudioMemberImplementationRefResponse> RecordedImplementationUpdates { get; } = [];

        public List<StudioMemberBindingRunStartRequest> StartedRuns { get; } = [];

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

        public Task StartBindingRunAsync(
            StudioMemberBindingRunStartRequest request,
            CancellationToken ct = default)
        {
            StartedRuns.Add(request);
            OperationsInOrder.Add("StartBindingRun");
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

}
