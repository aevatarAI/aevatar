using Aevatar.Configuration;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class AppScopedWorkflowServiceDeleteDraftTests
{
    [Fact]
    public async Task DeleteDraftAsync_ShouldCallWorkflowStorageDelete()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var storagePort = new RecordingWorkflowStoragePort();
        var service = environment.CreateService(workflowStoragePort: storagePort);

        await service.DeleteDraftAsync("scope-1", "workflow-1");

        storagePort.DeletedWorkflowIds.Should().Equal("workflow-1");
    }

    [Fact]
    public async Task DeleteDraftAsync_ShouldNotCallRuntimePorts()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var runtimePorts = new RuntimePortSpies();
        var service = environment.CreateService(
            workflowQueryPort: runtimePorts.QueryPort,
            workflowCommandPort: runtimePorts.CommandPort,
            workflowActorBindingReader: runtimePorts.BindingReader,
            artifactStore: runtimePorts.ArtifactStore,
            serviceLifecycleQueryPort: runtimePorts.ServiceLifecycleQueryPort,
            workflowStoragePort: new RecordingWorkflowStoragePort());

        await service.DeleteDraftAsync("scope-1", "workflow-1");

        runtimePorts.TotalInvocations.Should().Be(0);
    }

    [Fact]
    public async Task DeleteDraftAsync_ShouldRemoveLocalLayoutSidecarWhenPresent()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var service = environment.CreateService(workflowStoragePort: new RecordingWorkflowStoragePort());
        var layoutPath = environment.BuildLayoutPath("scope-1", "workflow-1");

        Directory.CreateDirectory(Path.GetDirectoryName(layoutPath)!);
        await File.WriteAllTextAsync(layoutPath, "{}");
        File.Exists(layoutPath).Should().BeTrue();

        await service.DeleteDraftAsync("scope-1", "workflow-1");

        File.Exists(layoutPath).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenStoragePortAndLayoutAreMissing_ShouldSucceedSilently()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var service = environment.CreateService();
        var layoutPath = environment.BuildLayoutPath("scope-1", "missing-workflow");

        var act = () => service.DeleteDraftAsync("scope-1", "missing-workflow");

        await act.Should().NotThrowAsync();
        File.Exists(layoutPath).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenStoragePortThrows_ShouldPropagateAndLeaveLayoutIntact()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var storagePort = new ThrowingWorkflowStoragePort(
            new InvalidOperationException("chrono-storage is unavailable"));
        var service = environment.CreateService(workflowStoragePort: storagePort);
        var layoutPath = environment.BuildLayoutPath("scope-1", "workflow-1");
        Directory.CreateDirectory(Path.GetDirectoryName(layoutPath)!);
        await File.WriteAllTextAsync(layoutPath, "{}");

        var act = () => service.DeleteDraftAsync("scope-1", "workflow-1");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("chrono-storage is unavailable");
        File.Exists(layoutPath).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenCancelled_ShouldPropagateOperationCanceledException()
    {
        using var environment = new ScopedWorkflowEnvironment();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var storagePort = new ThrowingWorkflowStoragePort(new OperationCanceledException(cts.Token));
        var service = environment.CreateService(workflowStoragePort: storagePort);

        var act = () => service.DeleteDraftAsync("scope-1", "workflow-1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class ScopedWorkflowEnvironment : IDisposable
    {
        private readonly string? _previousHome;

        public ScopedWorkflowEnvironment()
        {
            HomeDirectory = Path.Combine(Path.GetTempPath(), $"studio-scoped-delete-home-{Guid.NewGuid():N}");
            _previousHome = Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv);
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, HomeDirectory);
        }

        public string HomeDirectory { get; }

        public AppScopedWorkflowService CreateService(
            IScopeWorkflowQueryPort? workflowQueryPort = null,
            IScopeWorkflowCommandPort? workflowCommandPort = null,
            IWorkflowActorBindingReader? workflowActorBindingReader = null,
            IServiceRevisionArtifactStore? artifactStore = null,
            IServiceLifecycleQueryPort? serviceLifecycleQueryPort = null,
            IWorkflowStoragePort? workflowStoragePort = null)
        {
            return new AppScopedWorkflowService(
                new StubHttpClientFactory(),
                new StubWorkflowYamlDocumentService(),
                workflowQueryPort,
                workflowActorBindingReader,
                artifactStore,
                serviceLifecycleQueryPort,
                workflowStoragePort);
        }

        public string BuildLayoutPath(string scopeId, string workflowId) =>
            Path.Combine(
                AevatarPaths.Root,
                "app",
                "scope-workflow-layouts",
                $"{StudioDocumentIdNormalizer.Normalize(scopeId, "scope")}--{StudioDocumentIdNormalizer.Normalize(workflowId, "workflow")}.json");

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, _previousHome);
            if (Directory.Exists(HomeDirectory))
            {
                Directory.Delete(HomeDirectory, recursive: true);
            }
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("HTTP backend should not be called.");
    }

    private sealed class StubWorkflowYamlDocumentService : IWorkflowYamlDocumentService
    {
        public WorkflowParseResult Parse(string yaml) =>
            new(new WorkflowDocument { Name = "workflow" }, []);

        public string Serialize(WorkflowDocument document) =>
            $"name: {document.Name}\nsteps: []\n";
    }

    private sealed class RecordingWorkflowStoragePort : IWorkflowStoragePort
    {
        public List<string> DeletedWorkflowIds { get; } = [];

        public Task UploadWorkflowYamlAsync(string workflowId, string workflowName, string yaml, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<StoredWorkflowYaml>> ListWorkflowYamlsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StoredWorkflowYaml>>([]);

        public Task<StoredWorkflowYaml?> GetWorkflowYamlAsync(string workflowId, CancellationToken ct) =>
            Task.FromResult<StoredWorkflowYaml?>(null);

        public Task DeleteWorkflowYamlAsync(string workflowId, CancellationToken ct)
        {
            DeletedWorkflowIds.Add(workflowId);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingWorkflowStoragePort : IWorkflowStoragePort
    {
        private readonly Exception _exception;

        public ThrowingWorkflowStoragePort(Exception exception)
        {
            _exception = exception;
        }

        public Task UploadWorkflowYamlAsync(string workflowId, string workflowName, string yaml, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<StoredWorkflowYaml>> ListWorkflowYamlsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StoredWorkflowYaml>>([]);

        public Task<StoredWorkflowYaml?> GetWorkflowYamlAsync(string workflowId, CancellationToken ct) =>
            Task.FromResult<StoredWorkflowYaml?>(null);

        public Task DeleteWorkflowYamlAsync(string workflowId, CancellationToken ct) =>
            Task.FromException(_exception);
    }

    private sealed class RuntimePortSpies
    {
        public RuntimePortSpies()
        {
            QueryPort = new RecordingScopeWorkflowQueryPort(this);
            CommandPort = new RecordingScopeWorkflowCommandPort(this);
            BindingReader = new RecordingWorkflowActorBindingReader(this);
            ArtifactStore = new RecordingServiceRevisionArtifactStore(this);
            ServiceLifecycleQueryPort = new RecordingServiceLifecycleQueryPort(this);
        }

        public int TotalInvocations { get; private set; }

        public IScopeWorkflowQueryPort QueryPort { get; }

        public IScopeWorkflowCommandPort CommandPort { get; }

        public IWorkflowActorBindingReader BindingReader { get; }

        public IServiceRevisionArtifactStore ArtifactStore { get; }

        public IServiceLifecycleQueryPort ServiceLifecycleQueryPort { get; }

        public void RecordInvocation() => TotalInvocations += 1;
    }

    private sealed class RecordingScopeWorkflowQueryPort : IScopeWorkflowQueryPort
    {
        private readonly RuntimePortSpies _owner;

        public RecordingScopeWorkflowQueryPort(RuntimePortSpies owner)
        {
            _owner = owner;
        }

        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(string scopeId, CancellationToken ct = default)
        {
            _owner.RecordInvocation();
            return Task.FromResult<IReadOnlyList<ScopeWorkflowSummary>>([]);
        }

        public Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(string scopeId, string workflowId, CancellationToken ct = default)
        {
            _owner.RecordInvocation();
            return Task.FromResult<ScopeWorkflowSummary?>(null);
        }

        public Task<ScopeWorkflowSummary?> GetByActorIdAsync(string scopeId, string actorId, CancellationToken ct = default)
        {
            _owner.RecordInvocation();
            return Task.FromResult<ScopeWorkflowSummary?>(null);
        }
    }

    private sealed class RecordingScopeWorkflowCommandPort : IScopeWorkflowCommandPort
    {
        private readonly RuntimePortSpies _owner;

        public RecordingScopeWorkflowCommandPort(RuntimePortSpies owner)
        {
            _owner = owner;
        }

        public Task<ScopeWorkflowUpsertResult> UpsertAsync(ScopeWorkflowUpsertRequest request, CancellationToken ct = default)
        {
            _owner.RecordInvocation();
            throw new InvalidOperationException("Runtime command port should not be called.");
        }
    }

    private sealed class RecordingWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        private readonly RuntimePortSpies _owner;

        public RecordingWorkflowActorBindingReader(RuntimePortSpies owner)
        {
            _owner = owner;
        }

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            _owner.RecordInvocation();
            return Task.FromResult<WorkflowActorBinding?>(null);
        }
    }

    private sealed class RecordingServiceRevisionArtifactStore : IServiceRevisionArtifactStore
    {
        private readonly RuntimePortSpies _owner;

        public RecordingServiceRevisionArtifactStore(RuntimePortSpies owner)
        {
            _owner = owner;
        }

        public Task SaveAsync(string serviceKey, string revisionId, PreparedServiceRevisionArtifact artifact, CancellationToken ct = default)
        {
            _owner.RecordInvocation();
            return Task.CompletedTask;
        }

        public Task<PreparedServiceRevisionArtifact?> GetAsync(string serviceKey, string revisionId, CancellationToken ct = default)
        {
            _owner.RecordInvocation();
            return Task.FromResult<PreparedServiceRevisionArtifact?>(null);
        }
    }

    private sealed class RecordingServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        private readonly RuntimePortSpies _owner;

        public RecordingServiceLifecycleQueryPort(RuntimePortSpies owner)
        {
            _owner = owner;
        }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            _owner.RecordInvocation();
            return Task.FromResult<ServiceCatalogSnapshot?>(null);
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default)
        {
            _owner.RecordInvocation();
            return Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);
        }

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            _owner.RecordInvocation();
            return Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);
        }

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            _owner.RecordInvocation();
            return Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);
        }
    }
}
