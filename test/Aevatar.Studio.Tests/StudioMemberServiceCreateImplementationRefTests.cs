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

    public static IEnumerable<object[]> CreateTimeImplementationRefs()
    {
        yield return
        [
            MemberImplementationKindNames.Workflow,
            new StudioMemberImplementationRefResponse(
                ImplementationKind: MemberImplementationKindNames.Workflow,
                WorkflowId: "wf-alpha",
                WorkflowRevision: "rev-1"),
        ];
        yield return
        [
            MemberImplementationKindNames.Script,
            new StudioMemberImplementationRefResponse(
                ImplementationKind: MemberImplementationKindNames.Script,
                ScriptId: "script-alpha"),
        ];
        yield return
        [
            MemberImplementationKindNames.GAgent,
            new StudioMemberImplementationRefResponse(
                ImplementationKind: MemberImplementationKindNames.GAgent,
                DiagnosticActorTypeName: "Aevatar.SomeAgent"),
        ];
    }

    [Theory]
    [MemberData(nameof(CreateTimeImplementationRefs))]
    public async Task CreateAsync_ImplementationRef_ShouldRejectTypedExceptionBeforeCommandDispatch(
        string implementationKind,
        StudioMemberImplementationRefResponse implementationRef)
    {
        var commandPort = new RecordingMemberCommandPort();
        var service = NewService(commandPort);

        var act = () => service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: implementationKind,
                MemberId: "m-alpha",
                ImplementationRef: implementationRef));

        var thrown = await act.Should().ThrowAsync<StudioMemberCreateImplementationRefNotAllowedException>();
        thrown.Which.ScopeId.Should().Be(ScopeId);
        thrown.Which.Field.Should().Be("implementationRef");
        thrown.Which.Message.Should().Contain("create the member shell");
        thrown.Which.Message.Should().Contain("PUT /api/scopes/{scopeId}/members/{memberId}/binding");
        commandPort.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_ShellCreate_ShouldForwardNormalizedRequestToCommandPort()
    {
        var commandPort = new RecordingMemberCommandPort();
        var service = NewService(commandPort);

        var summary = await service.CreateAsync(
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: " Alpha ",
                ImplementationKind: " WORKFLOW ",
                Description: " first member ",
                MemberId: " m-alpha ",
                TeamId: " t-alpha "));

        commandPort.CreateRequests.Should().ContainSingle();
        commandPort.CreateRequests[0].ScopeId.Should().Be(ScopeId);
        commandPort.CreateRequests[0].Request.Should().Be(
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                Description: "first member",
                MemberId: "m-alpha",
                TeamId: "t-alpha",
                ImplementationRef: null));
        summary.LifecycleStage.Should().Be(MemberLifecycleStageNames.Created);
        summary.ImplementationRef.Should().BeNull();
    }

    private static StudioMemberService NewService(RecordingMemberCommandPort commandPort) =>
        new(
            commandPort,
            new ThrowingMemberQueryPort(),
            new ThrowingBindingRunQueryPort(),
            new InertTeamQueryPort(),
            new ThrowingServiceLifecycleQueryPort(),
            new ThrowingScopeBindingReadinessQueryPort(),
            new ThrowingServiceCommandPort(),
            new StudioWorkflowCapabilityAdmissionTestService());

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
                LifecycleStage: MemberLifecycleStageNames.Created,
                PublishedServiceId: "member-test",
                LastBoundRevisionId: null,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow)
            {
                ImplementationRef = null,
            });
        }

        public Task UpdateImplementationAsync(
            string scopeId,
            string memberId,
            StudioMemberImplementationRefResponse implementation,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("create must not dispatch implementation update.");

        public Task RecordPublishedBindingAsync(
            string scopeId,
            string memberId,
            StudioMemberPublishedBindingRecordRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("create must not record published bindings.");

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

        public Task DeleteAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("create must not delete members.");
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
