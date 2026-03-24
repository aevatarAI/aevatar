using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScopeWorkflowQueryApplicationServiceTests
{
    private const string ScopeId = "test-scope";
    private static readonly ScopeWorkflowCapabilityOptions DefaultOptions = new();

    [Fact]
    public async Task ListAsync_ShouldBuildSummariesFromServiceCatalogSnapshots()
    {
        var services = new[]
        {
            CreateServiceSnapshot("wf-a", "Workflow A", updatedAt: DateTimeOffset.UtcNow.AddMinutes(-10)),
            CreateServiceSnapshot("wf-b", "Workflow B", updatedAt: DateTimeOffset.UtcNow),
        };
        var lifecyclePort = new FakeServiceLifecycleQueryPort(listResult: services);
        var bindingReader = new FakeWorkflowActorBindingReader();
        var service = CreateService(lifecyclePort, bindingReader);

        var result = await service.ListAsync(ScopeId);

        result.Should().HaveCount(2);
        result[0].WorkflowId.Should().Be("wf-b", "newer service should come first");
        result[1].WorkflowId.Should().Be("wf-a");
    }

    [Fact]
    public async Task ListAsync_ShouldEnrichWithWorkflowBinding_WhenAvailable()
    {
        var snapshot = CreateServiceSnapshot("wf-enrich", "Enrich WF", primaryActorId: "actor-1");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(listResult: new[] { snapshot });
        var binding = new WorkflowActorBinding(
            ActorKind: WorkflowActorKind.Definition,
            ActorId: "actor-1",
            DefinitionActorId: "actor-1",
            RunId: "",
            WorkflowName: "enriched-workflow-name",
            WorkflowYaml: "yaml: true",
            InlineWorkflowYamls: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var bindingReader = new FakeWorkflowActorBindingReader(new Dictionary<string, WorkflowActorBinding>
        {
            ["actor-1"] = binding,
        });
        var service = CreateService(lifecyclePort, bindingReader);

        var result = await service.ListAsync(ScopeId);

        result.Should().ContainSingle();
        result[0].WorkflowName.Should().Be("enriched-workflow-name");
    }

    [Fact]
    public async Task ListAsync_ShouldReturnEmptyList_WhenNoServicesExist()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort(listResult: Array.Empty<ServiceCatalogSnapshot>());
        var bindingReader = new FakeWorkflowActorBindingReader();
        var service = CreateService(lifecyclePort, bindingReader);

        var result = await service.ListAsync(ScopeId);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByWorkflowIdAsync_ShouldReturnNull_WhenServiceNotFound()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var bindingReader = new FakeWorkflowActorBindingReader();
        var service = CreateService(lifecyclePort, bindingReader);

        var result = await service.GetByWorkflowIdAsync(ScopeId, "missing-wf");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByWorkflowIdAsync_ShouldReturnSummary_WhenServiceExists()
    {
        var snapshot = CreateServiceSnapshot(
            serviceId: "wf-found",
            displayName: "Found Workflow",
            activeRevisionId: "rev-5",
            deploymentId: "dep-1",
            primaryActorId: "actor-wf");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: snapshot);
        var bindingReader = new FakeWorkflowActorBindingReader();
        var service = CreateService(lifecyclePort, bindingReader);

        var result = await service.GetByWorkflowIdAsync(ScopeId, "wf-found");

        result.Should().NotBeNull();
        result!.ScopeId.Should().Be(ScopeId);
        result.WorkflowId.Should().Be("wf-found");
        result.DisplayName.Should().Be("Found Workflow");
        result.ActiveRevisionId.Should().Be("rev-5");
        result.DeploymentId.Should().Be("dep-1");
    }

    [Fact]
    public async Task GetByActorIdAsync_ShouldResolveRunToDefinitionActor()
    {
        var snapshot = CreateServiceSnapshot(
            serviceId: "wf-def",
            displayName: "Def Workflow",
            primaryActorId: "definition-actor-id");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(listResult: new[] { snapshot });
        var runBinding = new WorkflowActorBinding(
            ActorKind: WorkflowActorKind.Run,
            ActorId: "run-actor-id",
            DefinitionActorId: "definition-actor-id",
            RunId: "run-1",
            WorkflowName: "my-wf",
            WorkflowYaml: "",
            InlineWorkflowYamls: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var defBinding = new WorkflowActorBinding(
            ActorKind: WorkflowActorKind.Definition,
            ActorId: "definition-actor-id",
            DefinitionActorId: "definition-actor-id",
            RunId: "",
            WorkflowName: "my-wf",
            WorkflowYaml: "yaml: true",
            InlineWorkflowYamls: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var bindingReader = new FakeWorkflowActorBindingReader(new Dictionary<string, WorkflowActorBinding>
        {
            ["run-actor-id"] = runBinding,
            ["definition-actor-id"] = defBinding,
        });
        var service = CreateService(lifecyclePort, bindingReader);

        var result = await service.GetByActorIdAsync(ScopeId, "run-actor-id");

        result.Should().NotBeNull();
        result!.ActorId.Should().Be("definition-actor-id");
    }

    [Fact]
    public async Task GetByActorIdAsync_ShouldReturnNull_WhenBindingNotFound()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            listResult: Array.Empty<ServiceCatalogSnapshot>());
        var bindingReader = new FakeWorkflowActorBindingReader();
        var service = CreateService(lifecyclePort, bindingReader);

        var result = await service.GetByActorIdAsync(ScopeId, "unknown-actor");

        result.Should().BeNull();
    }

    private static ScopeWorkflowQueryApplicationService CreateService(
        FakeServiceLifecycleQueryPort lifecyclePort,
        FakeWorkflowActorBindingReader bindingReader) =>
        new(
            lifecyclePort,
            bindingReader,
            Options.Create(new ScopeWorkflowCapabilityOptions()));

    private static ServiceCatalogSnapshot CreateServiceSnapshot(
        string serviceId,
        string displayName,
        DateTimeOffset? updatedAt = null,
        string activeRevisionId = "rev-1",
        string deploymentId = "dep-default",
        string primaryActorId = "actor-default")
    {
        var options = new ScopeWorkflowCapabilityOptions();
        var ns = options.BuildNamespace(ScopeId);
        var serviceKey = ServiceKeys.Build(options.TenantId, options.AppId, ns, serviceId);
        return new ServiceCatalogSnapshot(
            ServiceKey: serviceKey,
            TenantId: options.TenantId,
            AppId: options.AppId,
            Namespace: ns,
            ServiceId: serviceId,
            DisplayName: displayName,
            DefaultServingRevisionId: activeRevisionId,
            ActiveServingRevisionId: activeRevisionId,
            DeploymentId: deploymentId,
            PrimaryActorId: primaryActorId,
            DeploymentStatus: "active",
            Endpoints: Array.Empty<ServiceEndpointSnapshot>(),
            PolicyIds: Array.Empty<string>(),
            UpdatedAt: updatedAt ?? DateTimeOffset.UtcNow);
    }

    private sealed class FakeServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        private readonly IReadOnlyList<ServiceCatalogSnapshot> _listResult;
        private readonly ServiceCatalogSnapshot? _getResult;

        public FakeServiceLifecycleQueryPort(
            IReadOnlyList<ServiceCatalogSnapshot>? listResult = null,
            ServiceCatalogSnapshot? getResult = null)
        {
            _listResult = listResult ?? Array.Empty<ServiceCatalogSnapshot>();
            _getResult = getResult;
        }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            Task.FromResult(_getResult);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult(_listResult);

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);
    }

    private sealed class FakeWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        private readonly IReadOnlyDictionary<string, WorkflowActorBinding> _bindings;

        public FakeWorkflowActorBindingReader(
            IReadOnlyDictionary<string, WorkflowActorBinding>? bindings = null)
        {
            _bindings = bindings ?? new Dictionary<string, WorkflowActorBinding>();
        }

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default) =>
            Task.FromResult(_bindings.TryGetValue(actorId, out var binding) ? binding : null);
    }
}
