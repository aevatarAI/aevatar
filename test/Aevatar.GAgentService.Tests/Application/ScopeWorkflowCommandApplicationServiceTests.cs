using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Workflows;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScopeWorkflowCommandApplicationServiceTests
{
    private const string ScopeId = "test-scope";
    private const string WorkflowId = "my-workflow";
    private const string WorkflowYaml = "name: test\nsteps:\n  - run: echo hello";
    private static readonly ScopeWorkflowCapabilityOptions DefaultOptions = new();

    [Fact]
    public async Task UpsertAsync_ShouldCreateServiceAndFullRevisionLifecycle_WhenNew()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var queryPort = new FakeWorkflowQueryPort(getByWorkflowIdResult: null);
        var service = CreateService(commandPort, lifecyclePort, queryPort);

        var result = await service.UpsertAsync(new ScopeWorkflowUpsertRequest(
            ScopeId, WorkflowId, WorkflowYaml));

        commandPort.Calls.Should().HaveCount(6);
        commandPort.Calls[0].Method.Should().Be("CreateServiceAsync");
        commandPort.Calls[1].Method.Should().Be("CreateRevisionAsync");
        commandPort.Calls[2].Method.Should().Be("PrepareRevisionAsync");
        commandPort.Calls[3].Method.Should().Be("PublishRevisionAsync");
        commandPort.Calls[4].Method.Should().Be("SetDefaultServingRevisionAsync");
        commandPort.Calls[5].Method.Should().Be("ActivateServiceRevisionAsync");
        result.Workflow.ScopeId.Should().Be(ScopeId);
        result.Workflow.WorkflowId.Should().Be(WorkflowId);

        var createCommand = commandPort.Calls[0].Command.Should().BeOfType<CreateServiceDefinitionCommand>().Subject;
        createCommand.Spec.Identity.TenantId.Should().Be(ScopeId);
        createCommand.Spec.Identity.AppId.Should().Be(DefaultOptions.ServiceAppId);
        createCommand.Spec.Identity.Namespace.Should().Be(DefaultOptions.ServiceNamespace);
    }

    [Fact]
    public async Task UpsertAsync_ShouldUpdateService_WhenDisplayNameChanged()
    {
        var existingSnapshot = CreateServiceSnapshot(
            serviceId: WorkflowId,
            displayName: "Old Name");
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: existingSnapshot);
        var queryPort = new FakeWorkflowQueryPort(getByWorkflowIdResult: null);
        var service = CreateService(commandPort, lifecyclePort, queryPort);

        await service.UpsertAsync(new ScopeWorkflowUpsertRequest(
            ScopeId, WorkflowId, WorkflowYaml, DisplayName: "New Name"));

        commandPort.Calls.Should().Contain(c => c.Method == "UpdateServiceAsync");
        commandPort.Calls.Should().NotContain(c => c.Method == "CreateServiceAsync");
    }

    [Fact]
    public async Task UpsertAsync_ShouldReturnFallbackSummary_WhenQueryReturnsNull()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var queryPort = new FakeWorkflowQueryPort(getByWorkflowIdResult: null);
        var service = CreateService(commandPort, lifecyclePort, queryPort);

        var result = await service.UpsertAsync(new ScopeWorkflowUpsertRequest(
            ScopeId, WorkflowId, WorkflowYaml));

        result.Workflow.ScopeId.Should().Be(ScopeId);
        result.Workflow.WorkflowId.Should().Be(WorkflowId);
        result.Workflow.DisplayName.Should().Be(WorkflowId);
        result.Workflow.DeploymentStatus.Should().Be("active");
    }

    [Fact]
    public async Task UpsertAsync_ShouldThrow_WhenWorkflowYamlIsEmpty()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var queryPort = new FakeWorkflowQueryPort(getByWorkflowIdResult: null);
        var service = CreateService(commandPort, lifecyclePort, queryPort);

        var act = () => service.UpsertAsync(new ScopeWorkflowUpsertRequest(
            ScopeId, WorkflowId, ""));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static ScopeWorkflowCommandApplicationService CreateService(
        RecordingServiceCommandPort commandPort,
        FakeServiceLifecycleQueryPort lifecyclePort,
        FakeWorkflowQueryPort queryPort) =>
        new(
            commandPort,
            lifecyclePort,
            queryPort,
            Options.Create(new ScopeWorkflowCapabilityOptions()));

    private static ServiceCatalogSnapshot CreateServiceSnapshot(
        string serviceId,
        string displayName,
        string activeRevisionId = "rev-1",
        string deploymentId = "dep-default",
        string primaryActorId = "actor-default")
    {
        var options = new ScopeWorkflowCapabilityOptions();
        var serviceKey = Aevatar.GAgentService.Abstractions.Services.ServiceKeys.Build(
            ScopeId,
            options.ServiceAppId,
            options.ServiceNamespace,
            serviceId);
        return new ServiceCatalogSnapshot(
            ServiceKey: serviceKey,
            TenantId: ScopeId,
            AppId: options.ServiceAppId,
            Namespace: options.ServiceNamespace,
            ServiceId: serviceId,
            DisplayName: displayName,
            DefaultServingRevisionId: activeRevisionId,
            ActiveServingRevisionId: activeRevisionId,
            DeploymentId: deploymentId,
            PrimaryActorId: primaryActorId,
            DeploymentStatus: "active",
            Endpoints: Array.Empty<ServiceEndpointSnapshot>(),
            PolicyIds: Array.Empty<string>(),
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private sealed record CommandCall(string Method, object? Command);

    private sealed class RecordingServiceCommandPort : IServiceCommandPort
    {
        private static readonly ServiceCommandAcceptedReceipt DefaultReceipt =
            new("target-actor", "cmd-1", "correlation-1");

        public List<CommandCall> Calls { get; } = [];

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(CreateServiceDefinitionCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("CreateServiceAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(UpdateServiceDefinitionCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("UpdateServiceAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(CreateServiceRevisionCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("CreateRevisionAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(PrepareServiceRevisionCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("PrepareRevisionAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(PublishServiceRevisionCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("PublishRevisionAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(RetireServiceRevisionCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("RetireRevisionAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(SetDefaultServingRevisionCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("SetDefaultServingRevisionAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(ActivateServiceRevisionCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("ActivateServiceRevisionAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(DeactivateServiceDeploymentCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(ReplaceServiceServingTargetsCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(StartServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(AdvanceServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(PauseServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(ResumeServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(RollbackServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);
    }

    private sealed class FakeServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        private readonly ServiceCatalogSnapshot? _getResult;

        public FakeServiceLifecycleQueryPort(ServiceCatalogSnapshot? getResult)
        {
            _getResult = getResult;
        }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(_getResult);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);
    }

    private sealed class FakeWorkflowQueryPort : IScopeWorkflowQueryPort
    {
        private readonly ScopeWorkflowSummary? _getByWorkflowIdResult;

        public FakeWorkflowQueryPort(ScopeWorkflowSummary? getByWorkflowIdResult)
        {
            _getByWorkflowIdResult = getByWorkflowIdResult;
        }

        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScopeWorkflowSummary>>([]);

        public Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(string scopeId, string workflowId, CancellationToken ct = default) =>
            Task.FromResult(_getByWorkflowIdResult);

        public Task<ScopeWorkflowSummary?> GetByActorIdAsync(string scopeId, string actorId, CancellationToken ct = default) =>
            Task.FromResult<ScopeWorkflowSummary?>(null);
    }
}
