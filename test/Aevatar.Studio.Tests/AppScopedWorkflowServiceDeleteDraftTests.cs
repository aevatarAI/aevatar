using Aevatar.Configuration;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Tests.Shared;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class AppScopedWorkflowServiceDeleteDraftTests
{
    [Fact]
    public async Task DeleteDraftAsync_ShouldCallWorkspaceCommandPortWithExplicitScope()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var workspacePort = new RecordingStudioWorkspacePorts(new[]
        {
            new ScopedDraft(
                "scope-1",
                NewDraft(
                    "workflow-1",
                    "workflow-1",
                    "name: workflow-1\nsteps: []\n",
                    DateTimeOffset.UtcNow)),
        });
        var service = environment.CreateService(
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: workspacePort);

        await service.DeleteDraftAsync("scope-1", "workflow-1");

        var deleted = workspacePort.DeletedDrafts.Should().ContainSingle().Subject;
        deleted.ScopeId.Should().Be("scope-1");
        deleted.WorkflowId.Should().Be("workflow-1");
        deleted.ExpectedVersion.Should().Be(11);
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
            workspaceQueryPort: new RecordingStudioWorkspacePorts(new[]
            {
                new ScopedDraft(
                    "scope-1",
                    NewDraft(
                        "workflow-1",
                        "workflow-1",
                        "name: workflow-1\nsteps: []\n",
                        DateTimeOffset.UtcNow)),
            }),
            workspaceCommandPort: new RecordingStudioWorkspacePorts());

        await service.DeleteDraftAsync("scope-1", "workflow-1");

        runtimePorts.TotalInvocations.Should().Be(0);
    }

    [Fact]
    public async Task CreateDraftAsync_ShouldPersistScopedDraftWithoutCallingRuntimePorts()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var runtimePorts = new RuntimePortSpies();
        var workspacePort = new RecordingStudioWorkspacePorts();
        var service = environment.CreateService(
            workflowQueryPort: runtimePorts.QueryPort,
            workflowCommandPort: runtimePorts.CommandPort,
            workflowActorBindingReader: runtimePorts.BindingReader,
            artifactStore: runtimePorts.ArtifactStore,
            serviceLifecycleQueryPort: runtimePorts.ServiceLifecycleQueryPort,
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: workspacePort);

        var saved = await service.CreateDraftAsync(
            "scope-1",
            new SaveWorkflowDraftRequest(
                DirectoryId: "scope:scope-1",
                WorkflowName: "workflow-1",
                FileName: null,
                Yaml: "name: workflow-1\nsteps: []\n"));

        runtimePorts.TotalInvocations.Should().Be(0);
        workspacePort.SavedDrafts.Should().ContainSingle();
        workspacePort.SavedDrafts[0].ScopeId.Should().Be("scope-1");
        workspacePort.SavedDrafts[0].ExpectedVersion.Should().Be(11);
        saved.WorkflowId.Should().Be("workflow-1");
    }

    [Fact]
    public async Task ListDraftsAsync_WhenDraftHasTypedLayout_ShouldMarkWorkflowSummaryAsHavingLayout()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var runtimePorts = new RuntimePortSpies();
        var service = environment.CreateService(
            workflowQueryPort: runtimePorts.QueryPort,
            workspaceQueryPort: new RecordingStudioWorkspacePorts(new[]
            {
                new ScopedDraft(
                    "scope-1",
                    NewDraft(
                        "workflow-1",
                        "workflow-1",
                        "name: workflow-1\nsteps: []\n",
                        DateTimeOffset.UtcNow,
                        new WorkflowLayoutDocument
                        {
                            NodePositions =
                            {
                                ["start"] = new WorkflowNodeLayout(10, 20),
                            },
                        })),
            }));

        var summaries = await service.ListDraftsAsync("scope-1");

        summaries.Should().ContainSingle();
        summaries[0].WorkflowId.Should().Be("workflow-1");
        summaries[0].HasLayout.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteDraftAsync_ShouldDeleteTypedDraftWithoutTouchingLayoutSidecar()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var workspacePort = new RecordingStudioWorkspacePorts(new[]
        {
            new ScopedDraft(
                "scope-1",
                NewDraft(
                    "workflow-1",
                    "workflow-1",
                    "name: workflow-1\nsteps: []\n",
                    DateTimeOffset.UtcNow,
                    new WorkflowLayoutDocument
                    {
                        NodePositions =
                        {
                            ["start"] = new WorkflowNodeLayout(10, 20),
                        },
                    })),
        });
        var service = environment.CreateService(
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: workspacePort);

        await service.DeleteDraftAsync("scope-1", "workflow-1");

        var deleted = workspacePort.DeletedDrafts.Should().ContainSingle().Subject;
        deleted.ScopeId.Should().Be("scope-1");
        deleted.WorkflowId.Should().Be("workflow-1");
        deleted.ExpectedVersion.Should().Be(11);
        (await workspacePort.GetAsync("scope-1", CancellationToken.None)).Drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenDraftIsMissing_ShouldThrowWorkflowDraftNotFoundException()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var workspacePort = new RecordingStudioWorkspacePorts();
        var service = environment.CreateService(
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: workspacePort);

        var act = () => service.DeleteDraftAsync("scope-1", "missing-workflow");

        await act.Should().ThrowAsync<WorkflowDraftNotFoundException>();
        workspacePort.DeletedDrafts.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenStoragePortThrows_ShouldPropagateAndLeaveLayoutIntact()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var workspaceQueryPort = new RecordingStudioWorkspacePorts([
            new ScopedDraft(
                "scope-1",
                NewDraft(
                    "workflow-1",
                    "workflow-1",
                    "name: workflow-1\nsteps: []\n",
                    DateTimeOffset.UtcNow)),
        ]);
        var workspaceCommandPort = new ThrowingWorkspaceCommandPort(
            new InvalidOperationException("workspace command port is unavailable"));
        var service = environment.CreateService(
            workspaceQueryPort: workspaceQueryPort,
            workspaceCommandPort: workspaceCommandPort);
        var layoutPath = environment.BuildLayoutPath("scope-1", "workflow-1");
        Directory.CreateDirectory(Path.GetDirectoryName(layoutPath)!);
        await File.WriteAllTextAsync(layoutPath, "{}");

        var act = () => service.DeleteDraftAsync("scope-1", "workflow-1");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("workspace command port is unavailable");
        File.Exists(layoutPath).Should().BeTrue();
    }

    [Fact]
    public async Task CreateDraftAsync_WhenStoragePortThrows_ShouldPropagateAndNotWriteLayoutSidecar()
    {
        using var environment = new ScopedWorkflowEnvironment();
        var workspacePort = new ThrowingWorkspaceQueryPort(
            new InvalidOperationException("workspace query port is unavailable"));
        var service = environment.CreateService(
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: new RecordingStudioWorkspacePorts());
        var layoutPath = environment.BuildLayoutPath("scope-1", "workflow-1");

        var act = () => service.CreateDraftAsync(
            "scope-1",
            new SaveWorkflowDraftRequest(
                DirectoryId: "scope:scope-1",
                WorkflowName: "workflow-1",
                FileName: null,
                Yaml: "name: workflow-1\nsteps: []\n"));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("workspace query port is unavailable");
        File.Exists(layoutPath).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenCancelled_ShouldPropagateOperationCanceledException()
    {
        using var environment = new ScopedWorkflowEnvironment();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var workspacePort = new ThrowingWorkspaceQueryPort(new OperationCanceledException(cts.Token));
        var service = environment.CreateService(
            workspaceQueryPort: workspacePort,
            workspaceCommandPort: new RecordingStudioWorkspacePorts());

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
            IStudioWorkspaceQueryPort? workspaceQueryPort = null,
            IStudioWorkspaceCommandPort? workspaceCommandPort = null)
        {
            return new AppScopedWorkflowService(
                new StubHttpClientFactory(),
                new StubWorkflowYamlDocumentService(),
                workflowQueryPort,
                workflowActorBindingReader,
                artifactStore,
                serviceLifecycleQueryPort,
                workspaceQueryPort,
                workspaceCommandPort);
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

    private sealed class ThrowingWorkspaceQueryPort : IStudioWorkspaceQueryPort
    {
        private readonly Exception _exception;

        public ThrowingWorkspaceQueryPort(Exception exception)
        {
            _exception = exception;
        }

        public Task<StudioWorkspaceSnapshot> GetAsync(CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceSnapshot>(_exception);

        public Task<StudioWorkspaceSnapshot> GetAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceSnapshot>(_exception);
    }

    private sealed class ThrowingWorkspaceCommandPort : IStudioWorkspaceCommandPort
    {
        private readonly Exception _exception;

        public ThrowingWorkspaceCommandPort(Exception exception)
        {
            _exception = exception;
        }

        public Task<StudioWorkspaceCommandReceipt> UpdateSettingsAsync(StudioWorkspaceSettings settings, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> AddDirectoryAsync(StudioWorkspaceDirectory directory, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> RemoveDirectoryAsync(string directoryId, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(StudioWorkflowDraftRecord draft, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(string scopeId, StudioWorkflowDraftRecord draft, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(string workflowId, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);

        public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(string scopeId, string workflowId, long? expectedVersion = null, CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(_exception);
    }

    private static StudioWorkflowDraftRecord NewDraft(
        string workflowId,
        string name,
        string yaml,
        DateTimeOffset updatedAtUtc,
        WorkflowLayoutDocument? layout = null) =>
        new(
            workflowId,
            name,
            $"{workflowId}.yaml",
            $"scope://scope-1/{workflowId}.yaml",
            "scope:scope-1",
            "scope-1",
            yaml,
            layout,
            updatedAtUtc,
            updatedAtUtc,
            1);

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
