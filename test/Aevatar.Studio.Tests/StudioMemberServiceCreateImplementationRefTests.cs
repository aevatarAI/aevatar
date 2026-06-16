using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberServiceCreateImplementationRefTests
{
    private const string ScopeId = "scope-1";

    [Fact]
    public async Task CreateAsync_WorkflowImplementationRef_ShouldForwardNormalizedRefToCommandPort()
    {
        var commandPort = new RecordingMemberCommandPort();
        var service = NewService(commandPort);

        await service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: " Alpha ",
                ImplementationKind: " WORKFLOW ",
                MemberId: " m-alpha ",
                ImplementationRef: new StudioMemberImplementationRefResponse(
                    ImplementationKind: " WORKFLOW ",
                    WorkflowId: " wf-alpha ",
                    WorkflowRevision: " rev-1 ")));

        commandPort.CreateRequests.Should().ContainSingle();
        commandPort.CreateRequests[0].Request.Should().BeEquivalentTo(
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                MemberId: "m-alpha",
                ImplementationRef: new StudioMemberImplementationRefResponse(
                    ImplementationKind: MemberImplementationKindNames.Workflow,
                    WorkflowId: "wf-alpha",
                    WorkflowRevision: "rev-1")));
    }

    [Fact]
    public async Task CreateAsync_ScriptImplementationRef_ShouldAllowOptionalRevision()
    {
        var commandPort = new RecordingMemberCommandPort();
        var service = NewService(commandPort);

        await service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Script Member",
                ImplementationKind: MemberImplementationKindNames.Script,
                ImplementationRef: new StudioMemberImplementationRefResponse(
                    ImplementationKind: MemberImplementationKindNames.Script,
                    ScriptId: " script-alpha ")));

        commandPort.CreateRequests.Should().ContainSingle();
        commandPort.CreateRequests[0].Request.ImplementationRef.Should().Be(
            new StudioMemberImplementationRefResponse(
                ImplementationKind: MemberImplementationKindNames.Script,
                ScriptId: "script-alpha"));
    }

    [Fact]
    public async Task CreateAsync_GAgentImplementationRef_ShouldAcceptDiagnosticActorTypeName()
    {
        var commandPort = new RecordingMemberCommandPort();
        var service = NewService(commandPort);

        await service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Agent Member",
                ImplementationKind: MemberImplementationKindNames.GAgent,
                ImplementationRef: new StudioMemberImplementationRefResponse(
                    ImplementationKind: MemberImplementationKindNames.GAgent,
                    DiagnosticActorTypeName: " Aevatar.SomeAgent ")));

        commandPort.CreateRequests[0].Request.ImplementationRef.Should().Be(
            new StudioMemberImplementationRefResponse(
                ImplementationKind: MemberImplementationKindNames.GAgent,
                DiagnosticActorTypeName: "Aevatar.SomeAgent"));
    }

    [Fact]
    public async Task CreateAsync_ImplementationRef_ShouldRejectKindMismatch()
    {
        var service = NewService(new RecordingMemberCommandPort());

        var act = () => service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                ImplementationRef: new StudioMemberImplementationRefResponse(
                    ImplementationKind: MemberImplementationKindNames.Script,
                    ScriptId: "script-alpha")));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*implementationRef.implementationKind must match implementationKind 'workflow'*");
    }

    [Fact]
    public async Task CreateAsync_WorkflowImplementationRef_ShouldRejectNonWorkflowFields()
    {
        var service = NewService(new RecordingMemberCommandPort());

        var act = () => service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                ImplementationRef: new StudioMemberImplementationRefResponse(
                    ImplementationKind: MemberImplementationKindNames.Workflow,
                    WorkflowId: "wf-alpha",
                    ScriptId: "script-alpha")));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*implementationRef.scriptId is not allowed*");
    }

    private static StudioMemberService NewService(RecordingMemberCommandPort commandPort) =>
        new(
            commandPort,
            new ThrowingMemberQueryPort(),
            new ThrowingBindingRunQueryPort(),
            new InertTeamQueryPort(),
            new ThrowingServiceLifecycleQueryPort(),
            new ThrowingScopeBindingReadinessQueryPort(),
            new ThrowingServiceCommandPort());

    private sealed class RecordingMemberCommandPort : IStudioMemberCommandPort
    {
        public List<CreateCall> CreateRequests { get; } = [];

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId,
            CreateStudioMemberRequest request,
            CancellationToken ct = default)
        {
            CreateRequests.Add(new CreateCall(scopeId, request));
            return Task.FromResult(new StudioMemberSummaryResponse(
                MemberId: request.MemberId ?? "m-generated",
                ScopeId: scopeId,
                DisplayName: request.DisplayName,
                Description: request.Description ?? string.Empty,
                ImplementationKind: request.ImplementationKind,
                LifecycleStage: request.ImplementationRef == null
                    ? MemberLifecycleStageNames.Created
                    : MemberLifecycleStageNames.BuildReady,
                PublishedServiceId: "member-test",
                LastBoundRevisionId: null,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow)
            {
                ImplementationRef = request.ImplementationRef,
            });
        }

        public Task UpdateImplementationAsync(
            string scopeId,
            string memberId,
            StudioMemberImplementationRefResponse implementation,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("create must not dispatch implementation update.");

        public Task StartBindingRunAsync(
            StudioMemberBindingRunStartRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("create must not start binding runs.");

        public Task PatchTeamAssignmentAsync(
            string scopeId,
            string memberId,
            string? targetTeamId,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("create must not patch team assignment.");

        public Task RenameAsync(
            string scopeId,
            string memberId,
            string displayName,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("create must not rename members.");
    }

    private sealed record CreateCall(string ScopeId, CreateStudioMemberRequest Request);

    private sealed class ThrowingMemberQueryPort : IStudioMemberQueryPort
    {
        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("create must not list members.");

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("create must not query members.");
    }

    private sealed class ThrowingBindingRunQueryPort : IStudioMemberBindingRunQueryPort
    {
        public Task<StudioMemberBindingRunStatusResponse?> GetAsync(
            string scopeId,
            string memberId,
            string bindingRunId,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("create must not query binding runs.");
    }

    private sealed class InertTeamQueryPort : IStudioTeamQueryPort
    {
        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId,
            StudioTeamRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, []));

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId,
            string teamId,
            CancellationToken ct = default) =>
            Task.FromResult<StudioTeamSummaryResponse?>(new StudioTeamSummaryResponse(
                TeamId: teamId,
                ScopeId: scopeId,
                DisplayName: "Team",
                Description: string.Empty,
                LifecycleStage: TeamLifecycleStageNames.Active,
                MemberCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow));
    }

    private sealed class ThrowingScopeBindingReadinessQueryPort : IScopeBindingReadinessQueryPort
    {
        public Task<ScopeBindingReadinessSnapshot> GetReadinessAsync(
            ScopeBindingReadinessRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("create must not query readiness.");
    }

    private sealed class ThrowingServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        public Task<ServiceCatalogSnapshot?> GetServiceAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("create must not query service lifecycle.");

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("create must not list services.");

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("create must not query revisions.");

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("create must not query deployments.");
    }

    private sealed class ThrowingServiceCommandPort : IServiceCommandPort
    {
        private static InvalidOperationException Reject(string method) =>
            new($"create must not call {method}.");

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(CreateServiceDefinitionCommand command, CancellationToken ct = default) => throw Reject(nameof(CreateServiceAsync));
        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(UpdateServiceDefinitionCommand command, CancellationToken ct = default) => throw Reject(nameof(UpdateServiceAsync));
        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(CreateServiceRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(CreateRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(PrepareServiceRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(PrepareRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(PublishServiceRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(PublishRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(RetireServiceRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(RetireRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(SetDefaultServingRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(SetDefaultServingRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(ActivateServiceRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(ActivateServiceRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(DeactivateServiceDeploymentCommand command, CancellationToken ct = default) => throw Reject(nameof(DeactivateServiceDeploymentAsync));
        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(ReplaceServiceServingTargetsCommand command, CancellationToken ct = default) => throw Reject(nameof(ReplaceServiceServingTargetsAsync));
        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(StartServiceRolloutCommand command, CancellationToken ct = default) => throw Reject(nameof(StartServiceRolloutAsync));
        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(AdvanceServiceRolloutCommand command, CancellationToken ct = default) => throw Reject(nameof(AdvanceServiceRolloutAsync));
        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(PauseServiceRolloutCommand command, CancellationToken ct = default) => throw Reject(nameof(PauseServiceRolloutAsync));
        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(ResumeServiceRolloutCommand command, CancellationToken ct = default) => throw Reject(nameof(ResumeServiceRolloutAsync));
        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(RollbackServiceRolloutCommand command, CancellationToken ct = default) => throw Reject(nameof(RollbackServiceRolloutAsync));
    }
}
