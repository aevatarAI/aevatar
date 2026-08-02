using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.Workflow.Abstractions;
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
        result[0].WorkflowId.Should().Be("wf-b");
        lifecyclePort.LastListRequest.Should().BeEquivalentTo(new FakeServiceLifecycleQueryPort.ListRequest(
            ScopeId,
            DefaultOptions.ServiceAppId,
            DefaultOptions.ServiceNamespace,
            DefaultOptions.ListTake));
    }

    [Fact]
    public async Task ListAsync_ShouldEnrichWithWorkflowBinding_WhenAvailable()
    {
        var snapshot = CreateServiceSnapshot("wf-enrich", "Enrich WF", primaryActorId: "actor-1");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(listResult: new[] { snapshot });
        var bindingReader = new FakeWorkflowActorBindingReader(new Dictionary<string, WorkflowActorBinding>
        {
            ["actor-1"] = new(
                ActorKind: WorkflowActorKind.Definition,
                ActorId: "actor-1",
                DefinitionActorId: "actor-1",
                RunId: "",
                WorkflowName: "enriched-workflow-name",
                WorkflowYaml: "yaml: true",
                InlineWorkflowYamls: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive),
        });
        var service = CreateService(lifecyclePort, bindingReader);

        var result = await service.ListAsync(ScopeId);

        result.Should().ContainSingle();
        result[0].WorkflowName.Should().Be("enriched-workflow-name");
    }

    [Fact]
    public async Task LookupByWorkflowIdAsync_ShouldReturnNotFound_WhenServiceCatalogMissing()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort();
        var service = CreateService(lifecyclePort, new FakeWorkflowActorBindingReader());

        var result = await service.LookupByWorkflowIdAsync(ScopeId, "wf-missing");

        result.Status.Should().Be(ScopeWorkflowLookupStatus.NotFound);
        result.Workflow.Should().BeNull();
    }

    [Fact]
    public async Task LookupByWorkflowIdAsync_ShouldReturnNotReady_WhenRuntimeFactsMissing()
    {
        var snapshot = CreateServiceSnapshot(
            serviceId: "wf-runtime-missing",
            displayName: "Runtime Missing",
            activeRevisionId: "",
            deploymentId: "",
            primaryActorId: "");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: snapshot);
        var service = CreateService(lifecyclePort, new FakeWorkflowActorBindingReader());

        var result = await service.LookupByWorkflowIdAsync(ScopeId, "wf-runtime-missing");

        result.Status.Should().Be(ScopeWorkflowLookupStatus.NotReady);
        result.Workflow.Should().BeNull();
    }

    [Fact]
    public async Task LookupByWorkflowIdAsync_ShouldReturnNotReady_WhenActorBindingMissing()
    {
        var snapshot = CreateServiceSnapshot(
            serviceId: "wf-no-binding",
            displayName: "No Binding",
            activeRevisionId: "rev-5",
            deploymentId: "dep-1",
            primaryActorId: "actor-wf");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            getResult: snapshot,
            deploymentResult: CreateDeploymentCatalog(snapshot));
        var service = CreateService(lifecyclePort, new FakeWorkflowActorBindingReader());

        var result = await service.LookupByWorkflowIdAsync(ScopeId, "wf-no-binding");

        result.Status.Should().Be(ScopeWorkflowLookupStatus.NotReady);
        result.Workflow.Should().BeNull();
    }

    [Fact]
    public async Task LookupByWorkflowIdAsync_ShouldReturnStale_WhenDeploymentReadModelMismatchesCatalog()
    {
        var snapshot = CreateServiceSnapshot(
            serviceId: "wf-stale",
            displayName: "Stale",
            activeRevisionId: "rev-5",
            deploymentId: "dep-1",
            primaryActorId: "actor-wf");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            getResult: snapshot,
            deploymentResult: CreateDeploymentCatalog(snapshot, revisionId: "rev-old"));
        var bindingReader = new FakeWorkflowActorBindingReader(CreateBinding("actor-wf", "workflow-name"));
        var service = CreateService(lifecyclePort, bindingReader);

        var result = await service.LookupByWorkflowIdAsync(ScopeId, "wf-stale");

        result.Status.Should().Be(ScopeWorkflowLookupStatus.Stale);
        result.Workflow.Should().BeNull();
    }

    [Fact]
    public async Task LookupByWorkflowIdAsync_ShouldReturnRunnable_WhenAllRuntimeFactsAreMaterialized()
    {
        var snapshot = CreateServiceSnapshot(
            serviceId: "wf-found",
            displayName: "Found Workflow",
            activeRevisionId: "rev-5",
            deploymentId: "dep-1",
            primaryActorId: "actor-wf");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            getResult: snapshot,
            deploymentResult: CreateDeploymentCatalog(snapshot));
        var bindingReader = new FakeWorkflowActorBindingReader(CreateBinding("actor-wf", "workflow-name"));
        var service = CreateService(lifecyclePort, bindingReader);

        var result = await service.LookupByWorkflowIdAsync(ScopeId, "wf-found");

        result.Status.Should().Be(ScopeWorkflowLookupStatus.Runnable);
        result.Workflow.Should().NotBeNull();
        result.Workflow!.ScopeId.Should().Be(ScopeId);
        result.Workflow.WorkflowId.Should().Be("wf-found");
        result.Workflow.DisplayName.Should().Be("Found Workflow");
        result.Workflow.WorkflowName.Should().Be("workflow-name");
        result.Workflow.ActorId.Should().Be("actor-wf");
        result.Workflow.ActiveRevisionId.Should().Be("rev-5");
        result.Workflow.DeploymentId.Should().Be("dep-1");
    }

    [Fact]
    public async Task GetByWorkflowIdAsync_ShouldReturnNull_WhenLookupIsNotRunnable()
    {
        var snapshot = CreateServiceSnapshot(
            serviceId: "wf-no-binding",
            displayName: "No Binding",
            activeRevisionId: "rev-5",
            deploymentId: "dep-1",
            primaryActorId: "actor-wf");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            getResult: snapshot,
            deploymentResult: CreateDeploymentCatalog(snapshot));
        var service = CreateService(lifecyclePort, new FakeWorkflowActorBindingReader());

        var result = await service.GetByWorkflowIdAsync(ScopeId, "wf-no-binding");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByActorIdAsync_ShouldResolveRunToDefinitionActor()
    {
        var snapshot = CreateServiceSnapshot(
            serviceId: "wf-def",
            displayName: "Def Workflow",
            primaryActorId: "definition-actor-id");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            listResult: new[] { snapshot },
            deploymentResult: CreateDeploymentCatalog(snapshot));
        var bindingReader = new FakeWorkflowActorBindingReader(new Dictionary<string, WorkflowActorBinding>
        {
            ["run-actor-id"] = new(
                ActorKind: WorkflowActorKind.Run,
                ActorId: "run-actor-id",
                DefinitionActorId: "definition-actor-id",
                RunId: "run-1",
                WorkflowName: "my-wf",
                WorkflowYaml: "",
                InlineWorkflowYamls: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive),
            ["definition-actor-id"] = new(
                ActorKind: WorkflowActorKind.Definition,
                ActorId: "definition-actor-id",
                DefinitionActorId: "definition-actor-id",
                RunId: "",
                WorkflowName: "my-wf",
                WorkflowYaml: "yaml: true",
                InlineWorkflowYamls: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive),
        });
        var service = CreateService(lifecyclePort, bindingReader);

        var result = await service.GetByActorIdAsync(ScopeId, "run-actor-id");

        result.Should().NotBeNull();
        result!.ActorId.Should().Be("definition-actor-id");
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
        var serviceKey = ServiceKeys.Build(ScopeId, options.ServiceAppId, options.ServiceNamespace, serviceId);
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
            DeploymentStatus: ServiceDeploymentStatus.Active.ToString(),
            Endpoints: Array.Empty<ServiceEndpointSnapshot>(),
            PolicyIds: Array.Empty<string>(),
            UpdatedAt: updatedAt ?? DateTimeOffset.UtcNow);
    }

    private static ServiceDeploymentCatalogSnapshot CreateDeploymentCatalog(
        ServiceCatalogSnapshot snapshot,
        string? revisionId = null,
        string? primaryActorId = null,
        string? status = null) =>
        new(
            snapshot.ServiceKey,
            [new ServiceDeploymentSnapshot(
                snapshot.DeploymentId,
                revisionId ?? snapshot.ActiveServingRevisionId,
                primaryActorId ?? snapshot.PrimaryActorId,
                status ?? ServiceDeploymentStatus.Active.ToString(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow);

    private static IReadOnlyDictionary<string, WorkflowActorBinding> CreateBinding(
        string actorId,
        string workflowName) =>
        new Dictionary<string, WorkflowActorBinding>(StringComparer.Ordinal)
        {
            [actorId] = new(
                ActorKind: WorkflowActorKind.Definition,
                ActorId: actorId,
                DefinitionActorId: actorId,
                RunId: string.Empty,
                WorkflowName: workflowName,
                WorkflowYaml: "yaml: true",
                InlineWorkflowYamls: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive),
        };

    private sealed class FakeServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        private readonly IReadOnlyList<ServiceCatalogSnapshot> _listResult;
        private readonly ServiceCatalogSnapshot? _getResult;
        private readonly ServiceDeploymentCatalogSnapshot? _deploymentResult;
        public ListRequest? LastListRequest { get; private set; }

        public FakeServiceLifecycleQueryPort(
            IReadOnlyList<ServiceCatalogSnapshot>? listResult = null,
            ServiceCatalogSnapshot? getResult = null,
            ServiceDeploymentCatalogSnapshot? deploymentResult = null)
        {
            _listResult = listResult ?? Array.Empty<ServiceCatalogSnapshot>();
            _getResult = getResult;
            _deploymentResult = deploymentResult;
        }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(_getResult ?? _listResult.FirstOrDefault(x => string.Equals(x.ServiceId, identity.ServiceId, StringComparison.Ordinal)));

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default)
        {
            LastListRequest = new ListRequest(tenantId, appId, @namespace, take);
            return Task.FromResult(_listResult);
        }

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(_deploymentResult);

        public sealed record ListRequest(string TenantId, string AppId, string Namespace, int Take);
    }

    private sealed class FakeWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        private readonly IReadOnlyDictionary<string, WorkflowActorBinding> _bindings;

        public FakeWorkflowActorBindingReader(IReadOnlyDictionary<string, WorkflowActorBinding>? bindings = null)
        {
            _bindings = bindings ?? new Dictionary<string, WorkflowActorBinding>(StringComparer.Ordinal);
        }

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            _bindings.TryGetValue(actorId, out var binding);
            return Task.FromResult(binding);
        }
    }
}
