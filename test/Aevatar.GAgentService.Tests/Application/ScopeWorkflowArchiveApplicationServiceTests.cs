using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Workflows;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScopeWorkflowArchiveApplicationServiceTests
{
    [Fact]
    public async Task ArchiveAsync_ShouldResolvePublishedIdentityAndDeactivateAuthoritativeDeployment()
    {
        var workflow = ActiveWorkflow();
        var queryPort = new StubScopeWorkflowQueryPort(new ScopeWorkflowLookupResult(
            ScopeWorkflowLookupStatus.Runnable,
            workflow,
            "runnable"));
        var commandPort = new RecordingServiceCommandPort();
        var service = new ScopeWorkflowArchiveApplicationService(queryPort, commandPort);

        var result = await service.ArchiveAsync(
            new ScopeWorkflowArchiveRequest("scope-alpha", "wf-alpha"));

        queryPort.Lookup.Should().Be(("scope-alpha", "wf-alpha"));
        commandPort.DeactivateCommand.Should().NotBeNull();
        commandPort.DeactivateCommand!.Identity.TenantId.Should().Be("scope-alpha");
        commandPort.DeactivateCommand.Identity.AppId.Should().Be("workflow-app");
        commandPort.DeactivateCommand.Identity.Namespace.Should().Be("workflow-namespace");
        commandPort.DeactivateCommand.Identity.ServiceId.Should().Be("svc-alpha");
        commandPort.DeactivateCommand.DeploymentId.Should().Be("dep-alpha");
        result.ScopeId.Should().Be("scope-alpha");
        result.WorkflowId.Should().Be("wf-alpha");
        result.DeploymentId.Should().Be("dep-alpha");
        result.CommandHandle.Stage.Should().Be("deactivate_deployment");
        result.CommandHandle.CommandId.Should().Be("cmd-archive");
        result.ReadModelUrl.Should().Be("/api/scopes/scope-alpha/workflows/wf-alpha");
        result.AcceptanceStage.Should().Be("accepted");
        result.PropagationStage.Should().Be("readmodel_propagating");
    }

    [Fact]
    public async Task ArchiveAsync_ShouldRejectWorkflowResolvedFromAnotherScopeWithoutDispatch()
    {
        var queryPort = new StubScopeWorkflowQueryPort(new ScopeWorkflowLookupResult(
            ScopeWorkflowLookupStatus.Runnable,
            ActiveWorkflow() with { ScopeId = "scope-other" },
            "runnable"));
        var commandPort = new RecordingServiceCommandPort();
        var service = new ScopeWorkflowArchiveApplicationService(queryPort, commandPort);

        var act = () => service.ArchiveAsync(
            new ScopeWorkflowArchiveRequest("scope-alpha", "wf-alpha"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*another scope*");
        commandPort.DeactivateCommand.Should().BeNull();
    }

    [Theory]
    [InlineData(ScopeWorkflowLookupStatus.NotFound, "service_catalog_missing")]
    [InlineData(ScopeWorkflowLookupStatus.NotReady, "deployment_readmodel_missing")]
    [InlineData(ScopeWorkflowLookupStatus.Stale, "published_service_descriptor_ambiguous")]
    public async Task ArchiveAsync_ShouldRejectUnavailableWorkflowWithoutDispatch(
        ScopeWorkflowLookupStatus status,
        string reason)
    {
        var queryPort = new StubScopeWorkflowQueryPort(new ScopeWorkflowLookupResult(
            status,
            Workflow: null,
            reason));
        var commandPort = new RecordingServiceCommandPort();
        var service = new ScopeWorkflowArchiveApplicationService(queryPort, commandPort);

        var act = () => service.ArchiveAsync(
            new ScopeWorkflowArchiveRequest("scope-alpha", "wf-alpha"));

        var error = await act.Should().ThrowAsync<ScopeWorkflowArchiveRejectedException>()
            .WithMessage($"*{reason}*");
        error.Which.Code.Should().Be(status switch
        {
            ScopeWorkflowLookupStatus.NotFound => "SCOPE_WORKFLOW_NOT_FOUND",
            ScopeWorkflowLookupStatus.NotReady => "WORKFLOW_ARCHIVE_NOT_READY",
            _ => "WORKFLOW_ARCHIVE_STALE",
        });
        commandPort.DeactivateCommand.Should().BeNull();
    }

    [Fact]
    public async Task ArchiveAsync_ShouldRejectNonActiveDeploymentWithoutDispatch()
    {
        var queryPort = new StubScopeWorkflowQueryPort(new ScopeWorkflowLookupResult(
            ScopeWorkflowLookupStatus.Runnable,
            ActiveWorkflow() with { DeploymentStatus = "Deactivated" },
            "runnable"));
        var commandPort = new RecordingServiceCommandPort();
        var service = new ScopeWorkflowArchiveApplicationService(queryPort, commandPort);

        var act = () => service.ArchiveAsync(
            new ScopeWorkflowArchiveRequest("scope-alpha", "wf-alpha"));

        var error = await act.Should().ThrowAsync<ScopeWorkflowArchiveRejectedException>()
            .WithMessage("*active deployment*");
        error.Which.Code.Should().Be("WORKFLOW_NOT_ACTIVE");
        commandPort.DeactivateCommand.Should().BeNull();
    }

    [Theory]
    [InlineData(nameof(ScopeWorkflowSummary.PublishedServiceId))]
    [InlineData(nameof(ScopeWorkflowSummary.ServiceAppId))]
    [InlineData(nameof(ScopeWorkflowSummary.ServiceNamespace))]
    [InlineData(nameof(ScopeWorkflowSummary.DeploymentId))]
    public async Task ArchiveAsync_ShouldRejectMissingPublishedIdentityComponentWithoutDispatch(
        string missingComponent)
    {
        var queryPort = new StubScopeWorkflowQueryPort(new ScopeWorkflowLookupResult(
            ScopeWorkflowLookupStatus.Runnable,
            ActiveWorkflowWithMissingIdentityComponent(missingComponent),
            "runnable"));
        var commandPort = new RecordingServiceCommandPort();
        var service = new ScopeWorkflowArchiveApplicationService(queryPort, commandPort);

        var act = () => service.ArchiveAsync(
            new ScopeWorkflowArchiveRequest("scope-alpha", "wf-alpha"));

        var error = await act.Should().ThrowAsync<ScopeWorkflowArchiveRejectedException>();
        error.Which.Code.Should().Be("WORKFLOW_ARCHIVE_IDENTITY_UNAVAILABLE");
        commandPort.DeactivateCommand.Should().BeNull();
    }

    private static ScopeWorkflowSummary ActiveWorkflowWithMissingIdentityComponent(string missingComponent)
    {
        var workflow = ActiveWorkflow();
        return missingComponent switch
        {
            nameof(ScopeWorkflowSummary.PublishedServiceId) => workflow with { PublishedServiceId = string.Empty },
            nameof(ScopeWorkflowSummary.ServiceAppId) => workflow with { ServiceAppId = string.Empty },
            nameof(ScopeWorkflowSummary.ServiceNamespace) => workflow with { ServiceNamespace = string.Empty },
            nameof(ScopeWorkflowSummary.DeploymentId) => workflow with { DeploymentId = string.Empty },
            _ => throw new ArgumentOutOfRangeException(nameof(missingComponent), missingComponent, null),
        };
    }

    private static ScopeWorkflowSummary ActiveWorkflow() =>
        new(
            ScopeId: "scope-alpha",
            WorkflowId: "wf-alpha",
            DisplayName: "Alpha",
            ServiceKey: "opaque-service-key",
            WorkflowName: "alpha",
            ActorId: "m-alpha",
            ActiveRevisionId: "rev-alpha",
            DeploymentId: "dep-alpha",
            DeploymentStatus: ServiceDeploymentStatus.Active.ToString(),
            UpdatedAt: DateTimeOffset.Parse("2026-08-10T10:00:00Z"))
        {
            PublishedServiceId = "svc-alpha",
            ServiceAppId = "workflow-app",
            ServiceNamespace = "workflow-namespace",
        };

    private sealed class StubScopeWorkflowQueryPort(ScopeWorkflowLookupResult result)
        : IScopeWorkflowQueryPort
    {
        public (string ScopeId, string WorkflowId)? Lookup { get; private set; }

        public Task<ScopeWorkflowLookupResult> LookupByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default)
        {
            Lookup = (scopeId, workflowId);
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(
            string scopeId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScopeWorkflowSummary>>([]);

        public Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default) =>
            Task.FromResult<ScopeWorkflowSummary?>(null);

        public Task<ScopeWorkflowSummary?> GetByActorIdAsync(
            string scopeId,
            string actorId,
            CancellationToken ct = default) =>
            Task.FromResult<ScopeWorkflowSummary?>(null);
    }

    private sealed class RecordingServiceCommandPort : IServiceCommandPort
    {
        public DeactivateServiceDeploymentCommand? DeactivateCommand { get; private set; }

        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(
            DeactivateServiceDeploymentCommand command,
            CancellationToken ct = default)
        {
            DeactivateCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt(
                "deployment-actor",
                "cmd-archive",
                "corr-archive"));
        }

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(
            CreateServiceDefinitionCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(
            UpdateServiceDefinitionCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(
            CreateServiceRevisionCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(
            PrepareServiceRevisionCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(
            PublishServiceRevisionCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(
            RetireServiceRevisionCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(
            ActivateServiceRevisionCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(
            ReplaceServiceServingTargetsCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(
            StartServiceRolloutCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(
            AdvanceServiceRolloutCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(
            PauseServiceRolloutCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(
            ResumeServiceRolloutCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(
            RollbackServiceRolloutCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
