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
/// Locks in the orchestration invariants for the new member-first
/// contract / activate / retire surface (issue #454):
///
///   - Every operation resolves the member-owned <c>publishedServiceId</c>
///     through <see cref="IStudioMemberQueryPort"/>; callers never pass a
///     serviceId, and the service ID we hand to the platform ports is the
///     member's stable <c>publishedServiceId</c>, never the raw memberId.
///   - The contract response builds member-first invoke paths, not the
///     legacy /services/{serviceId}/ path.
///   - Activate dispatches both <c>SetDefaultServingRevision</c> and
///     <c>ActivateServiceRevision</c> against the member's identity.
///   - Retired revisions cannot be re-activated.
///   - Retire dispatches <c>RetireServiceRevision</c> against the member's
///     identity, after verifying the revision exists.
///   - Missing members → typed 404; missing revisions / not-yet-bound →
///     <see cref="InvalidOperationException"/> (which endpoints map to 400).
/// </summary>
public sealed class StudioMemberServiceContractAndRevisionTests
{
    private const string ScopeId = "scope-1";
    private const string MemberId = "m-contract";
    private const string PublishedServiceId = "member-m-contract";

    [Fact]
    public async Task GetEndpointContractAsync_ShouldUseMemberPublishedServiceIdAndMemberInvokePath()
    {
        var detail = NewDetail();
        var queryPort = new InMemoryMemberQueryPort(detail);
        var lifecycle = new InMemoryServiceLifecycleQueryPort
        {
            Service = NewService(
                endpoints:
                [
                    new ServiceEndpointSnapshot(
                        EndpointId: "chat",
                        DisplayName: "Chat",
                        Kind: "chat",
                        RequestTypeUrl: "type.googleapis.com/x.Request",
                        ResponseTypeUrl: "type.googleapis.com/x.Response",
                        Description: string.Empty),
                ]),
            Revisions = NewRevisions(
                implementationKind: ServiceImplementationKind.Workflow,
                endpoints:
                [
                    new ServiceEndpointSnapshot(
                        EndpointId: "chat",
                        DisplayName: "Chat",
                        Kind: "chat",
                        RequestTypeUrl: "type.googleapis.com/x.Request",
                        ResponseTypeUrl: "type.googleapis.com/x.Response",
                        Description: string.Empty),
                ]),
        };
        var commandPort = new RecordingServiceCommandPort();

        var service = new StudioMemberService(
            new InertMemberCommandPort(),
            queryPort,
            new InertBindingRunQueryPort(),
            new InertTeamQueryPort(),
            lifecycle,
            commandPort);

        var contract = await service.GetEndpointContractAsync(ScopeId, MemberId, "chat", CancellationToken.None);

        contract.Should().NotBeNull();
        contract!.ScopeId.Should().Be(ScopeId);
        contract.MemberId.Should().Be(MemberId);
        // The platform-facing identity must be the member's stable
        // publishedServiceId — never the raw memberId — otherwise the
        // round-trip with Studio's bind path (which writes at member-{id})
        // would 404.
        contract.PublishedServiceId.Should().Be(PublishedServiceId);
        lifecycle.LastIdentity!.ServiceId.Should().Be(PublishedServiceId);

        // Frontend dispatches off InvokePath; for member-first contracts it
        // must be the member URL, not the legacy /services/{id}/invoke URL.
        contract.InvokePath.Should().Be($"/api/scopes/{ScopeId}/members/{MemberId}/invoke/chat:stream");
        contract.SupportsSse.Should().BeTrue();
        contract.StreamFrameFormat.Should().Be("workflow-run-event");
    }

    [Fact]
    public async Task GetEndpointContractAsync_ShouldReturnNullEndpoint_WhenEndpointNotFound()
    {
        var detail = NewDetail();
        var queryPort = new InMemoryMemberQueryPort(detail);
        var lifecycle = new InMemoryServiceLifecycleQueryPort
        {
            Service = NewService(endpoints: []),
            Revisions = NewRevisions(ServiceImplementationKind.Workflow, endpoints: []),
        };

        var service = new StudioMemberService(
            new InertMemberCommandPort(),
            queryPort,
            new InertBindingRunQueryPort(),
            new InertTeamQueryPort(),
            lifecycle,
            new RecordingServiceCommandPort());

        var contract = await service.GetEndpointContractAsync(ScopeId, MemberId, "ghost", CancellationToken.None);

        contract.Should().BeNull();
    }

    [Fact]
    public async Task GetEndpointContractAsync_ShouldThrowMemberNotFound_WhenMemberMissing()
    {
        var queryPort = new InMemoryMemberQueryPort(detail: null);
        var service = new StudioMemberService(
            new InertMemberCommandPort(),
            queryPort,
            new InertBindingRunQueryPort(),
            new InertTeamQueryPort(),
            new InMemoryServiceLifecycleQueryPort(),
            new RecordingServiceCommandPort());

        var act = () => service.GetEndpointContractAsync(ScopeId, "m-missing", "chat");

        await act.Should().ThrowAsync<StudioMemberNotFoundException>();
    }

    [Fact]
    public async Task GetEndpointContractAsync_ShouldThrowInvalidOperation_WhenMemberNotYetBound()
    {
        // Member exists but has not been bound, so the platform lifecycle
        // port has no service catalog entry. Surface as InvalidOperation,
        // which endpoints map to 400, distinct from 404 missing-member.
        var detail = NewDetail();
        var queryPort = new InMemoryMemberQueryPort(detail);
        var lifecycle = new InMemoryServiceLifecycleQueryPort { Service = null };
        var service = new StudioMemberService(
            new InertMemberCommandPort(),
            queryPort,
            new InertBindingRunQueryPort(),
            new InertTeamQueryPort(),
            lifecycle,
            new RecordingServiceCommandPort());

        var act = () => service.GetEndpointContractAsync(ScopeId, MemberId, "chat");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*has no published service yet*");
    }

    [Fact]
    public async Task ActivateBindingRevisionAsync_ShouldDispatchSetDefaultAndActivate()
    {
        var detail = NewDetail();
        var queryPort = new InMemoryMemberQueryPort(detail);
        var lifecycle = new InMemoryServiceLifecycleQueryPort
        {
            Service = NewService(endpoints: []),
            Revisions = NewRevisions(
                ServiceImplementationKind.Workflow,
                endpoints: [],
                revisionId: "rev-1",
                status: ServiceRevisionStatus.Created),
        };
        var commandPort = new RecordingServiceCommandPort();

        var service = new StudioMemberService(
            new InertMemberCommandPort(),
            queryPort,
            new InertBindingRunQueryPort(),
            new InertTeamQueryPort(),
            lifecycle,
            commandPort);

        var response = await service.ActivateBindingRevisionAsync(
            ScopeId,
            MemberId,
            "rev-1",
            CancellationToken.None);

        // Both commands must fire in order, both pinned to the member's
        // publishedServiceId — never the scope-default service.
        commandPort.OperationsInOrder.Should().Equal("SetDefaultServing", "Activate");
        commandPort.SetDefaultIdentities.Should().ContainSingle()
            .Which.ServiceId.Should().Be(PublishedServiceId);
        commandPort.ActivateIdentities.Should().ContainSingle()
            .Which.ServiceId.Should().Be(PublishedServiceId);
        commandPort.SetDefaultRevisionIds.Should().ContainSingle().Which.Should().Be("rev-1");
        commandPort.ActivateRevisionIds.Should().ContainSingle().Which.Should().Be("rev-1");

        response.MemberId.Should().Be(MemberId);
        response.PublishedServiceId.Should().Be(PublishedServiceId);
        response.RevisionId.Should().Be("rev-1");
    }

    [Fact]
    public async Task ActivateBindingRevisionAsync_ShouldRefuse_WhenRevisionRetired()
    {
        var detail = NewDetail();
        var queryPort = new InMemoryMemberQueryPort(detail);
        var lifecycle = new InMemoryServiceLifecycleQueryPort
        {
            Service = NewService(endpoints: []),
            Revisions = NewRevisions(
                ServiceImplementationKind.Workflow,
                endpoints: [],
                revisionId: "rev-r",
                status: ServiceRevisionStatus.Retired),
        };
        var commandPort = new RecordingServiceCommandPort();

        var service = new StudioMemberService(
            new InertMemberCommandPort(),
            queryPort,
            new InertBindingRunQueryPort(),
            new InertTeamQueryPort(),
            lifecycle,
            commandPort);

        var act = () => service.ActivateBindingRevisionAsync(ScopeId, MemberId, "rev-r");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*retired and cannot be activated*");
        // Guard must reject before any command is dispatched: a retired
        // revision must never be revived through Activate.
        commandPort.OperationsInOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task ActivateBindingRevisionAsync_ShouldThrowInvalidOperation_WhenRevisionMissing()
    {
        var detail = NewDetail();
        var queryPort = new InMemoryMemberQueryPort(detail);
        var lifecycle = new InMemoryServiceLifecycleQueryPort
        {
            Service = NewService(endpoints: []),
            Revisions = NewRevisions(
                ServiceImplementationKind.Workflow,
                endpoints: [],
                revisionId: "rev-other"),
        };
        var commandPort = new RecordingServiceCommandPort();

        var service = new StudioMemberService(
            new InertMemberCommandPort(),
            queryPort,
            new InertBindingRunQueryPort(),
            new InertTeamQueryPort(),
            lifecycle,
            commandPort);

        var act = () => service.ActivateBindingRevisionAsync(ScopeId, MemberId, "rev-missing");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*was not found*");
        commandPort.OperationsInOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task RetireBindingRevisionAsync_ShouldDispatchRetireOnMemberIdentity()
    {
        var detail = NewDetail();
        var queryPort = new InMemoryMemberQueryPort(detail);
        var lifecycle = new InMemoryServiceLifecycleQueryPort
        {
            Service = NewService(endpoints: []),
            Revisions = NewRevisions(
                ServiceImplementationKind.Workflow,
                endpoints: [],
                revisionId: "rev-9"),
        };
        var commandPort = new RecordingServiceCommandPort();

        var service = new StudioMemberService(
            new InertMemberCommandPort(),
            queryPort,
            new InertBindingRunQueryPort(),
            new InertTeamQueryPort(),
            lifecycle,
            commandPort);

        var response = await service.RetireBindingRevisionAsync(
            ScopeId, MemberId, "rev-9", CancellationToken.None);

        commandPort.OperationsInOrder.Should().Equal("Retire");
        commandPort.RetireIdentities.Should().ContainSingle()
            .Which.ServiceId.Should().Be(PublishedServiceId);
        commandPort.RetireRevisionIds.Should().ContainSingle().Which.Should().Be("rev-9");

        response.Status.Should().Be("retired");
        response.MemberId.Should().Be(MemberId);
        response.PublishedServiceId.Should().Be(PublishedServiceId);
        response.RevisionId.Should().Be("rev-9");
    }

    [Fact]
    public async Task RetireBindingRevisionAsync_ShouldRequireExistingRevision()
    {
        var detail = NewDetail();
        var queryPort = new InMemoryMemberQueryPort(detail);
        var lifecycle = new InMemoryServiceLifecycleQueryPort
        {
            Service = NewService(endpoints: []),
            Revisions = NewRevisions(
                ServiceImplementationKind.Workflow,
                endpoints: [],
                revisionId: "rev-other"),
        };
        var commandPort = new RecordingServiceCommandPort();

        var service = new StudioMemberService(
            new InertMemberCommandPort(),
            queryPort,
            new InertBindingRunQueryPort(),
            new InertTeamQueryPort(),
            lifecycle,
            commandPort);

        var act = () => service.RetireBindingRevisionAsync(ScopeId, MemberId, "rev-missing");

        await act.Should().ThrowAsync<InvalidOperationException>();
        commandPort.OperationsInOrder.Should().BeEmpty();
    }

    private static StudioMemberDetailResponse NewDetail()
    {
        var summary = new StudioMemberSummaryResponse(
            MemberId: MemberId,
            ScopeId: ScopeId,
            DisplayName: "Test Member",
            Description: string.Empty,
            ImplementationKind: MemberImplementationKindNames.Workflow,
            LifecycleStage: MemberLifecycleStageNames.BindReady,
            PublishedServiceId: PublishedServiceId,
            LastBoundRevisionId: "rev-1",
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt: DateTimeOffset.UtcNow.AddHours(-1));

        return new StudioMemberDetailResponse(
            Summary: summary,
            ImplementationRef: null,
            LastBinding: null);
    }

    private static ServiceCatalogSnapshot NewService(IReadOnlyList<ServiceEndpointSnapshot> endpoints) =>
        new(
            ServiceKey: $"{ScopeId}/{PublishedServiceId}",
            TenantId: ScopeId,
            AppId: "default",
            Namespace: "default",
            ServiceId: PublishedServiceId,
            DisplayName: "Test Member Service",
            DefaultServingRevisionId: "rev-1",
            ActiveServingRevisionId: "rev-1",
            DeploymentId: "dep-1",
            PrimaryActorId: "actor-1",
            DeploymentStatus: "Active",
            Endpoints: endpoints,
            PolicyIds: [],
            UpdatedAt: DateTimeOffset.UtcNow);

    private static ServiceRevisionCatalogSnapshot NewRevisions(
        ServiceImplementationKind implementationKind,
        IReadOnlyList<ServiceEndpointSnapshot> endpoints,
        string revisionId = "rev-1",
        ServiceRevisionStatus status = ServiceRevisionStatus.Created)
    {
        return new ServiceRevisionCatalogSnapshot(
            ServiceKey: $"{ScopeId}/{PublishedServiceId}",
            Revisions:
            [
                new ServiceRevisionSnapshot(
                    RevisionId: revisionId,
                    ImplementationKind: implementationKind.ToString(),
                    Status: status.ToString(),
                    ArtifactHash: "h",
                    FailureReason: string.Empty,
                    Endpoints: endpoints,
                    CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
                    PreparedAt: null,
                    PublishedAt: null,
                    RetiredAt: status == ServiceRevisionStatus.Retired ? DateTimeOffset.UtcNow : null),
            ],
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private sealed class InMemoryMemberQueryPort : IStudioMemberQueryPort
    {
        private readonly StudioMemberDetailResponse? _detail;

        public InMemoryMemberQueryPort(StudioMemberDetailResponse? detail)
        {
            _detail = detail;
        }

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId, StudioMemberRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberRosterResponse(scopeId, _detail == null ? [] : [_detail.Summary]));

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            Task.FromResult(_detail);
    }

    private sealed class InertBindingRunQueryPort : IStudioMemberBindingRunQueryPort
    {
        public Task<StudioMemberBindingRunStatusResponse?> GetAsync(
            string scopeId,
            string memberId,
            string bindingRunId,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("contract/activate/retire flows must not query binding runs.");
    }

    // Bind / impl-update commands are not exercised here; we route through
    // the member query port and platform service ports directly. Any
    // accidental fan-out into this surface should fail loudly.
    private sealed class InertMemberCommandPort : IStudioMemberCommandPort
    {
        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId, CreateStudioMemberRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("contract/activate/retire flows must not write to the member command port.");

        public Task UpdateImplementationAsync(
            string scopeId, string memberId,
            StudioMemberImplementationRefResponse implementation, CancellationToken ct = default) =>
            throw new InvalidOperationException("contract/activate/retire flows must not update implementation refs.");

        public Task StartBindingRunAsync(
            StudioMemberBindingRunStartRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("contract/activate/retire flows must not start binding runs.");

        public Task RecordBindingAsync(
            string scopeId, string memberId, string publishedServiceId,
            string revisionId, string implementationKindName, CancellationToken ct = default) =>
            throw new InvalidOperationException("contract/activate/retire flows must not record new bindings.");

        public Task ReassignTeamAsync(
            string scopeId, string memberId, string? fromTeamId, string? toTeamId,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("contract/activate/retire flows must not reassign teams.");
    }

    private sealed class InertTeamQueryPort : IStudioTeamQueryPort
    {
        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId, StudioTeamRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, []));

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId, string teamId, CancellationToken ct = default) =>
            Task.FromResult<StudioTeamSummaryResponse?>(null);
    }

    private sealed class InertScopeBindingCommandPort : IScopeBindingCommandPort
    {
        public Task<ScopeBindingUpsertResult> UpsertAsync(
            ScopeBindingUpsertRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("contract/activate/retire flows must not invoke the scope binding port.");
    }

    private sealed class InMemoryServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        public ServiceCatalogSnapshot? Service { get; set; }
        public ServiceRevisionCatalogSnapshot? Revisions { get; set; }
        public ServiceIdentity? LastIdentity { get; private set; }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(
            ServiceIdentity identity, CancellationToken ct = default)
        {
            LastIdentity = identity;
            return Task.FromResult(Service);
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(
            ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(Revisions);

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(
            ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);
    }

    private sealed class RecordingServiceCommandPort : IServiceCommandPort
    {
        public List<string> OperationsInOrder { get; } = [];
        public List<ServiceIdentity> SetDefaultIdentities { get; } = [];
        public List<string> SetDefaultRevisionIds { get; } = [];
        public List<ServiceIdentity> ActivateIdentities { get; } = [];
        public List<string> ActivateRevisionIds { get; } = [];
        public List<ServiceIdentity> RetireIdentities { get; } = [];
        public List<string> RetireRevisionIds { get; } = [];

        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(
            SetDefaultServingRevisionCommand command, CancellationToken ct = default)
        {
            SetDefaultIdentities.Add(command.Identity);
            SetDefaultRevisionIds.Add(command.RevisionId);
            OperationsInOrder.Add("SetDefaultServing");
            return Task.FromResult(NewReceipt());
        }

        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(
            ActivateServiceRevisionCommand command, CancellationToken ct = default)
        {
            ActivateIdentities.Add(command.Identity);
            ActivateRevisionIds.Add(command.RevisionId);
            OperationsInOrder.Add("Activate");
            return Task.FromResult(NewReceipt());
        }

        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(
            RetireServiceRevisionCommand command, CancellationToken ct = default)
        {
            RetireIdentities.Add(command.Identity);
            RetireRevisionIds.Add(command.RevisionId);
            OperationsInOrder.Add("Retire");
            return Task.FromResult(NewReceipt());
        }

        // Unused commands — assert via throw so a future regression that
        // routes through the wrong command makes the test red instead of
        // silently passing.
        private static InvalidOperationException Reject(string method) =>
            new($"contract/activate/retire flows must not call {method}.");

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

        private static ServiceCommandAcceptedReceipt NewReceipt() =>
            new("actor-1", "cmd-1", "corr-1");
    }
}
