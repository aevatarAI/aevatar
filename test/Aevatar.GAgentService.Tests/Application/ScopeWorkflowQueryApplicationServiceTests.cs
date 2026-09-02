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
    public async Task LookupByWorkflowIdAsync_ShouldUseCommittedDefaultServingTarget()
    {
        const string currentRevisionId = "rev-current";
        const string currentDeploymentId = "dep-current";
        const string currentActorId = "actor-current";
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateServiceSnapshot(
            serviceId: "wf-serving",
            displayName: "Serving Workflow",
            activeRevisionId: string.Empty,
            deploymentId: string.Empty,
            primaryActorId: string.Empty,
            defaultServingRevisionId: currentRevisionId);
        var deployments = new ServiceDeploymentCatalogSnapshot(
            snapshot.ServiceKey,
            [
                new ServiceDeploymentSnapshot(
                    "dep-stale",
                    "rev-stale",
                    "actor-stale",
                    ServiceDeploymentStatus.Active.ToString(),
                    now,
                    now),
                new ServiceDeploymentSnapshot(
                    currentDeploymentId,
                    currentRevisionId,
                    currentActorId,
                    ServiceDeploymentStatus.Active.ToString(),
                    now.AddDays(-1),
                    now.AddDays(-1)),
            ],
            now);
        var servingSet = new ServiceServingSetSnapshot(
            snapshot.ServiceKey,
            Generation: 7,
            ActiveRolloutId: string.Empty,
            Targets:
            [
                new ServiceServingTargetSnapshot(
                    currentDeploymentId,
                    currentRevisionId,
                    currentActorId,
                    AllocationWeight: 100,
                    ServiceServingState.Active.ToString(),
                    EnabledEndpointIds: []),
            ],
            UpdatedAt: now);
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            getResult: snapshot,
            deploymentResult: deployments,
            servingResult: servingSet);
        var bindingReader = new FakeWorkflowActorBindingReader(
            CreateBinding(currentActorId, "serving-workflow"));
        var service = CreateService(lifecyclePort, bindingReader);

        var result = await service.LookupByWorkflowIdAsync(ScopeId, "wf-serving");

        result.Status.Should().Be(ScopeWorkflowLookupStatus.Runnable);
        result.Workflow.Should().NotBeNull();
        result.Workflow!.ActiveRevisionId.Should().Be(currentRevisionId);
        result.Workflow.DeploymentId.Should().Be(currentDeploymentId);
        result.Workflow.ActorId.Should().Be(currentActorId);
    }

    [Fact]
    public async Task LookupByWorkflowIdAsync_ShouldSelectServingTargetThatEnablesWorkflowChatEndpoint()
    {
        const string revisionId = "rev-current";
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateServiceSnapshot(
            serviceId: "wf-chat-target",
            displayName: "Chat Target Workflow",
            activeRevisionId: string.Empty,
            deploymentId: string.Empty,
            primaryActorId: string.Empty,
            defaultServingRevisionId: revisionId);
        var deployments = new ServiceDeploymentCatalogSnapshot(
            snapshot.ServiceKey,
            [
                new ServiceDeploymentSnapshot(
                    "dep-without-chat",
                    revisionId,
                    "actor-without-chat",
                    ServiceDeploymentStatus.Active.ToString(),
                    now,
                    now),
                new ServiceDeploymentSnapshot(
                    "dep-with-chat",
                    revisionId,
                    "actor-with-chat",
                    ServiceDeploymentStatus.Active.ToString(),
                    now,
                    now),
            ],
            now);
        var servingSet = new ServiceServingSetSnapshot(
            snapshot.ServiceKey,
            Generation: 8,
            ActiveRolloutId: string.Empty,
            Targets:
            [
                new ServiceServingTargetSnapshot(
                    "dep-without-chat",
                    revisionId,
                    "actor-without-chat",
                    AllocationWeight: 100,
                    ServiceServingState.Active.ToString(),
                    EnabledEndpointIds: ["admin"]),
                new ServiceServingTargetSnapshot(
                    "dep-with-chat",
                    revisionId,
                    "actor-with-chat",
                    AllocationWeight: 25,
                    ServiceServingState.Active.ToString(),
                    EnabledEndpointIds: ["chat"]),
            ],
            UpdatedAt: now);
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            getResult: snapshot,
            deploymentResult: deployments,
            servingResult: servingSet);
        var bindingReader = new FakeWorkflowActorBindingReader(
            CreateBinding("actor-with-chat", "chat-target-workflow"));
        var service = CreateService(lifecyclePort, bindingReader);

        var result = await service.LookupByWorkflowIdAsync(ScopeId, "wf-chat-target");

        result.Status.Should().Be(ScopeWorkflowLookupStatus.Runnable);
        result.Workflow.Should().NotBeNull();
        result.Workflow!.DeploymentId.Should().Be("dep-with-chat");
        result.Workflow.ActorId.Should().Be("actor-with-chat");
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
    public async Task LookupByWorkflowIdAsync_ShouldResolveExplicitPublishedServiceDescriptor()
    {
        const string workflowId = "wf-alpha";
        const string publishedServiceId = "svc-alpha";
        var snapshot = CreateServiceSnapshot(
            publishedServiceId,
            "Studio Workflow",
            activeRevisionId: "rev-alpha",
            deploymentId: "dep-alpha",
            primaryActorId: "actor-alpha",
            appId: "studio");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            getResult: snapshot,
            deploymentResult: CreateDeploymentCatalog(snapshot));
        var bindingReader = new FakeWorkflowActorBindingReader(
            CreateBinding("actor-alpha", "studio-workflow", workflowId));
        var descriptorSource = new FakePublishedServiceDescriptorSource(
            new ScopeWorkflowPublishedServiceDescriptor(
                ScopeId,
                workflowId,
                "studio",
                DefaultOptions.ServiceNamespace,
                publishedServiceId,
                "Studio Workflow",
                DateTimeOffset.UtcNow));
        var service = CreateService(lifecyclePort, bindingReader, descriptorSource);

        var result = await service.LookupByWorkflowIdAsync(ScopeId, workflowId);

        result.Status.Should().Be(ScopeWorkflowLookupStatus.Runnable);
        result.Workflow.Should().NotBeNull();
        result.Workflow!.WorkflowId.Should().Be(workflowId);
        result.Workflow.ServiceKey.Should().Be(snapshot.ServiceKey);
        result.Workflow.ServiceAppId.Should().Be("studio");
        result.Workflow.PublishedServiceId.Should().Be(publishedServiceId);
        lifecyclePort.LastGetRequest.Should().NotBeNull();
        lifecyclePort.LastGetRequest!.AppId.Should().Be("studio");
        lifecyclePort.LastGetRequest.ServiceId.Should().Be(publishedServiceId);
        lifecyclePort.LastGetRequest.ServiceId.Should().NotBe(workflowId);
    }

    [Fact]
    public async Task LookupByWorkflowIdAsync_ShouldRejectDescriptor_WhenBindingUsesPublishedServiceIdAsWorkflowId()
    {
        const string workflowId = "dinner_date";
        const string publishedServiceId = "default";
        var snapshot = CreateServiceSnapshot(
            publishedServiceId,
            "Dinner Date Mock",
            activeRevisionId: "dinner-date-mock-v2",
            deploymentId: "dep-dinner",
            primaryActorId: "actor-dinner");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            getResult: snapshot,
            deploymentResult: CreateDeploymentCatalog(snapshot));
        var bindingReader = new FakeWorkflowActorBindingReader(
            CreateBinding("actor-dinner", "dinner_date_mock", publishedServiceId));
        var descriptorSource = new FakePublishedServiceDescriptorSource(
            new ScopeWorkflowPublishedServiceDescriptor(
                ScopeId,
                workflowId,
                DefaultOptions.ServiceAppId,
                DefaultOptions.ServiceNamespace,
                publishedServiceId,
                "Dinner Date Mock",
                DateTimeOffset.UtcNow));
        var service = CreateService(lifecyclePort, bindingReader, descriptorSource);

        var result = await service.LookupByWorkflowIdAsync(ScopeId, workflowId);

        result.Status.Should().Be(ScopeWorkflowLookupStatus.Stale);
        result.Workflow.Should().BeNull();
        result.Reason.Should().Be("workflow_actor_binding_workflow_mismatched");
        lifecyclePort.LastGetRequest.Should().NotBeNull();
        lifecyclePort.LastGetRequest!.ServiceId.Should().Be(publishedServiceId);
    }

    [Fact]
    public async Task LookupCatalogueByWorkflowIdAsync_ShouldReturnCommittedServiceWithoutRunnableDeployment()
    {
        const string workflowId = "wf-alpha";
        var snapshot = CreateServiceSnapshot(
            "svc-alpha",
            "Studio Workflow",
            activeRevisionId: string.Empty,
            deploymentId: string.Empty,
            primaryActorId: string.Empty,
            appId: "studio") with
        {
            DeploymentStatus = "inactive",
        };
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: snapshot);
        var descriptorSource = new FakePublishedServiceDescriptorSource(
            new ScopeWorkflowPublishedServiceDescriptor(
                ScopeId,
                workflowId,
                "studio",
                DefaultOptions.ServiceNamespace,
                "svc-alpha",
                "Studio Workflow",
                DateTimeOffset.UtcNow));
        var service = CreateService(
            lifecyclePort,
            new FakeWorkflowActorBindingReader(),
            descriptorSource);

        var result = await service.LookupCatalogueByWorkflowIdAsync(ScopeId, workflowId);

        result.Status.Should().Be(ScopeWorkflowCatalogueLookupStatus.Found);
        result.Workflow.Should().NotBeNull();
        result.Workflow!.WorkflowId.Should().Be(workflowId);
        result.Workflow.ServiceAppId.Should().Be("studio");
        result.Workflow.ServiceNamespace.Should().Be(DefaultOptions.ServiceNamespace);
        result.Workflow.PublishedServiceId.Should().Be("svc-alpha");
        result.Workflow.DeploymentStatus.Should().Be("inactive");
        lifecyclePort.LastGetRequest.Should().NotBeNull();
        lifecyclePort.LastGetRequest!.ServiceId.Should().Be("svc-alpha");
    }

    [Fact]
    public async Task LookupCatalogueByWorkflowIdAsync_ShouldReportAmbiguousPublishedServices()
    {
        const string workflowId = "wf-alpha";
        var lifecyclePort = new FakeServiceLifecycleQueryPort();
        var descriptorSource = new FakePublishedServiceDescriptorSource(
            new ScopeWorkflowPublishedServiceDescriptor(
                ScopeId,
                workflowId,
                "studio",
                DefaultOptions.ServiceNamespace,
                "svc-alpha",
                "Studio Workflow A",
                DateTimeOffset.UtcNow),
            new ScopeWorkflowPublishedServiceDescriptor(
                ScopeId,
                workflowId,
                "studio",
                DefaultOptions.ServiceNamespace,
                "svc-beta",
                "Studio Workflow B",
                DateTimeOffset.UtcNow));
        var service = CreateService(
            lifecyclePort,
            new FakeWorkflowActorBindingReader(),
            descriptorSource);

        var result = await service.LookupCatalogueByWorkflowIdAsync(ScopeId, workflowId);

        result.Status.Should().Be(ScopeWorkflowCatalogueLookupStatus.Ambiguous);
        result.Workflow.Should().BeNull();
        lifecyclePort.LastGetRequest.Should().BeNull();
    }

    [Fact]
    public async Task LookupCatalogueByWorkflowIdAsync_ShouldNotTreatSameNamedServiceAsPublishedWorkflow()
    {
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            getResult: CreateServiceSnapshot("wf-missing", "Unrelated Service"));
        var service = CreateService(
            lifecyclePort,
            new FakeWorkflowActorBindingReader());

        var result = await service.LookupCatalogueByWorkflowIdAsync(ScopeId, "wf-missing");

        result.Status.Should().Be(ScopeWorkflowCatalogueLookupStatus.NotFound);
        result.Workflow.Should().BeNull();
        lifecyclePort.LastGetRequest.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldIncludeExplicitPublishedServiceDescriptor()
    {
        var snapshot = CreateServiceSnapshot(
            "svc-alpha",
            "Studio Workflow",
            primaryActorId: "actor-alpha",
            appId: "studio");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            getResult: snapshot,
            deploymentResult: CreateDeploymentCatalog(snapshot));
        var bindingReader = new FakeWorkflowActorBindingReader(
            CreateBinding("actor-alpha", "studio-workflow", "wf-alpha"));
        var descriptorSource = new FakePublishedServiceDescriptorSource(
            new ScopeWorkflowPublishedServiceDescriptor(
                ScopeId,
                "wf-alpha",
                "studio",
                DefaultOptions.ServiceNamespace,
                "svc-alpha",
                "Studio Workflow",
                DateTimeOffset.UtcNow));
        var service = CreateService(lifecyclePort, bindingReader, descriptorSource);

        var result = await service.ListAsync(ScopeId);

        result.Should().ContainSingle();
        result[0].WorkflowId.Should().Be("wf-alpha");
        result[0].ServiceKey.Should().Be(snapshot.ServiceKey);
    }

    [Fact]
    public async Task LookupByWorkflowIdAsync_ShouldPreferExplicitPublishedServiceDescriptorOverConventionalIdentity()
    {
        const string workflowId = "wf-alpha";
        var directSnapshot = CreateServiceSnapshot(workflowId, "Direct Workflow");
        var studioSnapshot = CreateServiceSnapshot(
            "svc-alpha",
            "Studio Workflow",
            primaryActorId: "actor-default",
            appId: "studio");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            listResult: [directSnapshot, studioSnapshot],
            deploymentResult: CreateDeploymentCatalog(studioSnapshot));
        var descriptorSource = new FakePublishedServiceDescriptorSource(
            new ScopeWorkflowPublishedServiceDescriptor(
                ScopeId,
                workflowId,
                "studio",
                DefaultOptions.ServiceNamespace,
                "svc-alpha",
                "Studio Workflow",
                DateTimeOffset.UtcNow));
        var service = CreateService(
            lifecyclePort,
            new FakeWorkflowActorBindingReader(CreateBinding("actor-default", "Studio Workflow", workflowId)),
            descriptorSource);

        var result = await service.LookupByWorkflowIdAsync(ScopeId, workflowId);

        result.Status.Should().Be(ScopeWorkflowLookupStatus.Runnable);
        result.Workflow.Should().NotBeNull();
        result.Workflow!.ServiceAppId.Should().Be("studio");
        result.Workflow.ServiceNamespace.Should().Be(DefaultOptions.ServiceNamespace);
        result.Workflow.PublishedServiceId.Should().Be("svc-alpha");
        lifecyclePort.LastGetRequest.Should().NotBeNull();
        lifecyclePort.LastGetRequest!.AppId.Should().Be("studio");
        lifecyclePort.LastGetRequest.ServiceId.Should().Be("svc-alpha");
    }

    [Fact]
    public async Task ListAsync_ShouldHideConventionalAndExplicitIdentityConflict()
    {
        const string workflowId = "wf-alpha";
        var directSnapshot = CreateServiceSnapshot(workflowId, "Direct Workflow");
        var studioSnapshot = CreateServiceSnapshot(
            "svc-alpha",
            "Studio Workflow",
            appId: "studio");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            listResult: [directSnapshot, studioSnapshot]);
        var descriptorSource = new FakePublishedServiceDescriptorSource(
            new ScopeWorkflowPublishedServiceDescriptor(
                ScopeId,
                workflowId,
                "studio",
                DefaultOptions.ServiceNamespace,
                "svc-alpha",
                "Studio Workflow",
                DateTimeOffset.UtcNow));
        var service = CreateService(
            lifecyclePort,
            new FakeWorkflowActorBindingReader(),
            descriptorSource);

        var result = await service.ListAsync(ScopeId);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task LookupByWorkflowIdAsync_ShouldRejectMismatchedActorBindingWorkflowIdentity()
    {
        var snapshot = CreateServiceSnapshot(
            "svc-alpha",
            "Studio Workflow",
            primaryActorId: "actor-alpha",
            appId: "studio");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            getResult: snapshot,
            deploymentResult: CreateDeploymentCatalog(snapshot));
        var bindingReader = new FakeWorkflowActorBindingReader(
            CreateBinding("actor-alpha", "studio-workflow", "wf-other"));
        var descriptorSource = new FakePublishedServiceDescriptorSource(
            new ScopeWorkflowPublishedServiceDescriptor(
                ScopeId,
                "wf-alpha",
                "studio",
                DefaultOptions.ServiceNamespace,
                "svc-alpha",
                "Studio Workflow",
                DateTimeOffset.UtcNow));
        var service = CreateService(lifecyclePort, bindingReader, descriptorSource);

        var result = await service.LookupByWorkflowIdAsync(ScopeId, "wf-alpha");

        result.Status.Should().Be(ScopeWorkflowLookupStatus.Stale);
        result.Reason.Should().Be("workflow_actor_binding_workflow_mismatched");
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
        FakeWorkflowActorBindingReader bindingReader,
        IScopeWorkflowPublishedServiceDescriptorSource? descriptorSource = null) =>
        new(
            lifecyclePort,
            lifecyclePort,
            bindingReader,
            Options.Create(new ScopeWorkflowCapabilityOptions()),
            descriptorSource == null ? null : [descriptorSource]);

    private static ServiceCatalogSnapshot CreateServiceSnapshot(
        string serviceId,
        string displayName,
        DateTimeOffset? updatedAt = null,
        string activeRevisionId = "rev-1",
        string deploymentId = "dep-default",
        string primaryActorId = "actor-default",
        string? appId = null,
        string? defaultServingRevisionId = null)
    {
        var options = new ScopeWorkflowCapabilityOptions();
        var resolvedAppId = appId ?? options.ServiceAppId;
        var serviceKey = ServiceKeys.Build(ScopeId, resolvedAppId, options.ServiceNamespace, serviceId);
        return new ServiceCatalogSnapshot(
            ServiceKey: serviceKey,
            TenantId: ScopeId,
            AppId: resolvedAppId,
            Namespace: options.ServiceNamespace,
            ServiceId: serviceId,
            DisplayName: displayName,
            DefaultServingRevisionId: defaultServingRevisionId ?? activeRevisionId,
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
        string workflowName,
        string workflowId = "") =>
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
                ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                WorkflowId: workflowId),
        };

    private sealed class FakeServiceLifecycleQueryPort : IServiceLifecycleQueryPort, IServiceServingQueryPort
    {
        private readonly IReadOnlyList<ServiceCatalogSnapshot> _listResult;
        private readonly ServiceCatalogSnapshot? _getResult;
        private readonly ServiceDeploymentCatalogSnapshot? _deploymentResult;
        private readonly ServiceServingSetSnapshot? _servingResult;
        public ListRequest? LastListRequest { get; private set; }
        public ServiceIdentity? LastGetRequest { get; private set; }

        public FakeServiceLifecycleQueryPort(
            IReadOnlyList<ServiceCatalogSnapshot>? listResult = null,
            ServiceCatalogSnapshot? getResult = null,
            ServiceDeploymentCatalogSnapshot? deploymentResult = null,
            ServiceServingSetSnapshot? servingResult = null)
        {
            _listResult = listResult ?? Array.Empty<ServiceCatalogSnapshot>();
            _getResult = getResult;
            _deploymentResult = deploymentResult;
            _servingResult = servingResult;
        }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastGetRequest = identity.Clone();
            var candidate = _getResult ?? _listResult.FirstOrDefault(x => MatchesIdentity(x, identity));
            return Task.FromResult(candidate != null && MatchesIdentity(candidate, identity) ? candidate : null);
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default)
        {
            LastListRequest = new ListRequest(tenantId, appId, @namespace, take);
            return Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>(_listResult
                .Where(snapshot =>
                    string.Equals(snapshot.TenantId, tenantId, StringComparison.Ordinal) &&
                    string.Equals(snapshot.AppId, appId, StringComparison.Ordinal) &&
                    string.Equals(snapshot.Namespace, @namespace, StringComparison.Ordinal))
                .Take(take)
                .ToArray());
        }

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(_deploymentResult);

        public Task<ServiceServingSetSnapshot?> GetServiceServingSetAsync(
            ServiceIdentity identity,
            CancellationToken ct = default)
        {
            if (_servingResult != null)
                return Task.FromResult<ServiceServingSetSnapshot?>(_servingResult);

            if (_deploymentResult == null)
                return Task.FromResult<ServiceServingSetSnapshot?>(null);

            return Task.FromResult<ServiceServingSetSnapshot?>(new ServiceServingSetSnapshot(
                _deploymentResult.ServiceKey,
                Generation: 1,
                ActiveRolloutId: string.Empty,
                Targets: _deploymentResult.Deployments
                    .Where(deployment => string.Equals(
                        deployment.Status,
                        ServiceDeploymentStatus.Active.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                    .Select(deployment => new ServiceServingTargetSnapshot(
                        deployment.DeploymentId,
                        deployment.RevisionId,
                        deployment.PrimaryActorId,
                        AllocationWeight: 100,
                        ServiceServingState.Active.ToString(),
                        EnabledEndpointIds: []))
                    .ToArray(),
                UpdatedAt: _deploymentResult.UpdatedAt));
        }

        public Task<ServiceRolloutSnapshot?> GetServiceRolloutAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceRolloutSnapshot?>(null);

        public Task<ServiceRolloutCommandObservationSnapshot?> GetServiceRolloutCommandObservationAsync(
            ServiceIdentity identity,
            string commandId,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceRolloutCommandObservationSnapshot?>(null);

        public Task<ServiceTrafficViewSnapshot?> GetServiceTrafficViewAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceTrafficViewSnapshot?>(null);

        public sealed record ListRequest(string TenantId, string AppId, string Namespace, int Take);

        private static bool MatchesIdentity(ServiceCatalogSnapshot snapshot, ServiceIdentity identity) =>
            string.Equals(snapshot.TenantId, identity.TenantId, StringComparison.Ordinal) &&
            string.Equals(snapshot.AppId, identity.AppId, StringComparison.Ordinal) &&
            string.Equals(snapshot.Namespace, identity.Namespace, StringComparison.Ordinal) &&
            string.Equals(snapshot.ServiceId, identity.ServiceId, StringComparison.Ordinal);
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

    private sealed class FakePublishedServiceDescriptorSource
        : IScopeWorkflowPublishedServiceDescriptorSource
    {
        private readonly IReadOnlyList<ScopeWorkflowPublishedServiceDescriptor> _descriptors;

        public FakePublishedServiceDescriptorSource(params ScopeWorkflowPublishedServiceDescriptor[] descriptors)
        {
            _descriptors = descriptors;
        }

        public Task<IReadOnlyList<ScopeWorkflowPublishedServiceDescriptor>> ListAsync(
            string scopeId,
            int take,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScopeWorkflowPublishedServiceDescriptor>>(
                _descriptors.Where(descriptor => descriptor.ScopeId == scopeId).Take(take).ToArray());

        public Task<IReadOnlyList<ScopeWorkflowPublishedServiceDescriptor>> FindByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScopeWorkflowPublishedServiceDescriptor>>(
                _descriptors.Where(descriptor =>
                    descriptor.ScopeId == scopeId && descriptor.WorkflowId == workflowId).ToArray());
    }
}
