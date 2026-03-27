using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScopeWorkflowApplicationServicesTests
{
    [Fact]
    public async Task UpsertAsync_ShouldCreateWorkflowServiceRevisionAndActivation()
    {
        var options = new ScopeWorkflowCapabilityOptions
        {
            ServiceAppId = "default",
            ServiceNamespace = "default",
            DefinitionActorIdPrefix = "scope-workflow",
        };
        var identity = new ServiceIdentity
        {
            TenantId = "external-user-1",
            AppId = options.ServiceAppId,
            Namespace = options.ServiceNamespace,
            ServiceId = "approval-flow",
        };
        const string revisionId = "rev-001";
        var expectedActorPrefix = options.BuildDefinitionActorIdPrefix("external-user-1", "approval-flow");
        var expectedDeploymentId = $"{ServiceActorIds.Deployment(identity)}:{revisionId}";
        var expectedActorId = $"{expectedActorPrefix}:{expectedDeploymentId}";

        var commandPort = new FakeServiceCommandPort();
        var queryPort = new FakeServiceLifecycleQueryPort();
        queryPort.GetServiceResults.Enqueue(null);
        queryPort.GetServiceResults.Enqueue(new ServiceCatalogSnapshot(
            ServiceKeys.Build(identity),
            identity.TenantId,
            identity.AppId,
            identity.Namespace,
            identity.ServiceId,
            "Approval Flow",
            revisionId,
            revisionId,
            expectedDeploymentId,
            expectedActorId,
            "active",
            [],
            [],
            DateTimeOffset.UtcNow));

        var queryService = new ScopeWorkflowQueryApplicationService(
            queryPort,
            new FakeWorkflowActorBindingReader(),
            Options.Create(options));
        var service = new ScopeWorkflowCommandApplicationService(
            commandPort,
            queryPort,
            queryService,
            Options.Create(options));

        var result = await service.UpsertAsync(new ScopeWorkflowUpsertRequest(
            "external-user-1",
            "approval-flow",
            "name: approval",
            WorkflowName: "approval",
            DisplayName: "Approval Flow",
            InlineWorkflowYamls: new Dictionary<string, string> { ["child.yaml"] = "name: child" },
            RevisionId: revisionId));

        result.Workflow.ScopeId.Should().Be("external-user-1");
        result.Workflow.ActorId.Should().Be(expectedActorId);
        result.DefinitionActorIdPrefix.Should().Be(expectedActorPrefix);
        commandPort.CreateServiceCommand!.Spec.Identity.Should().BeEquivalentTo(identity);
        commandPort.CreateRevisionCommand!.Spec.WorkflowSpec.DefinitionActorId.Should().Be(expectedActorPrefix);
    }

    [Fact]
    public async Task ListAsync_ShouldQueryScopeAndEnrichWorkflowNameFromBinding()
    {
        var options = new ScopeWorkflowCapabilityOptions();
        const string actorId = "scope-workflow:actor-1";
        var queryPort = new FakeServiceLifecycleQueryPort
        {
            ListServicesResult =
            [
                new ServiceCatalogSnapshot(
                    ServiceKeys.Build("external-user-2", options.ServiceAppId, options.ServiceNamespace, "approval-flow"),
                    "external-user-2",
                    options.ServiceAppId,
                    options.ServiceNamespace,
                    "approval-flow",
                    "Approval Flow",
                    "rev-1",
                    "rev-1",
                    "dep-1",
                    actorId,
                    "active",
                    [],
                    [],
                    DateTimeOffset.UtcNow),
            ],
        };
        var bindingReader = new FakeWorkflowActorBindingReader();
        bindingReader.Bindings[actorId] = new WorkflowActorBinding(
            WorkflowActorKind.Definition,
            actorId,
            actorId,
            string.Empty,
            "approval",
            "name: approval",
            new Dictionary<string, string>());

        var service = new ScopeWorkflowQueryApplicationService(
            queryPort,
            bindingReader,
            Options.Create(options));

        var workflows = await service.ListAsync("external-user-2");

        workflows.Should().ContainSingle();
        workflows[0].WorkflowId.Should().Be("approval-flow");
        workflows[0].WorkflowName.Should().Be("approval");
        queryPort.LastListRequest.Should().BeEquivalentTo(new FakeServiceLifecycleQueryPort.ListRequest(
            "external-user-2",
            options.ServiceAppId,
            options.ServiceNamespace,
            options.ListTake));
    }

    [Fact]
    public async Task GetByActorIdAsync_ShouldResolveRunActorBackToDefinitionActor()
    {
        var options = new ScopeWorkflowCapabilityOptions();
        const string definitionActorId = "scope-workflow:def-1";
        const string runActorId = "workflow-run:run-1";
        var queryPort = new FakeServiceLifecycleQueryPort
        {
            ListServicesResult =
            [
                new ServiceCatalogSnapshot(
                    ServiceKeys.Build("external-user-3", options.ServiceAppId, options.ServiceNamespace, "approval-flow"),
                    "external-user-3",
                    options.ServiceAppId,
                    options.ServiceNamespace,
                    "approval-flow",
                    "Approval Flow",
                    "rev-1",
                    "rev-1",
                    "dep-1",
                    definitionActorId,
                    "active",
                    [],
                    [],
                    DateTimeOffset.UtcNow),
            ],
        };
        var bindingReader = new FakeWorkflowActorBindingReader();
        bindingReader.Bindings[runActorId] = new WorkflowActorBinding(
            WorkflowActorKind.Run,
            runActorId,
            definitionActorId,
            "run-1",
            "approval",
            string.Empty,
            new Dictionary<string, string>());

        var service = new ScopeWorkflowQueryApplicationService(
            queryPort,
            bindingReader,
            Options.Create(options));

        var workflow = await service.GetByActorIdAsync("external-user-3", runActorId);

        workflow.Should().NotBeNull();
        workflow!.WorkflowId.Should().Be("approval-flow");
        workflow.ActorId.Should().Be(definitionActorId);
    }

    private sealed class FakeServiceCommandPort : IServiceCommandPort
    {
        public CreateServiceDefinitionCommand? CreateServiceCommand { get; private set; }
        public UpdateServiceDefinitionCommand? UpdateServiceCommand { get; private set; }
        public CreateServiceRevisionCommand? CreateRevisionCommand { get; private set; }
        public PrepareServiceRevisionCommand? PrepareRevisionCommand { get; private set; }
        public PublishServiceRevisionCommand? PublishRevisionCommand { get; private set; }
        public SetDefaultServingRevisionCommand? SetDefaultServingRevisionCommand { get; private set; }
        public ActivateServiceRevisionCommand? ActivateServiceRevisionCommand { get; private set; }

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(CreateServiceDefinitionCommand command, CancellationToken ct = default)
        {
            CreateServiceCommand = command;
            return Task.FromResult(Accepted());
        }

        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(UpdateServiceDefinitionCommand command, CancellationToken ct = default)
        {
            UpdateServiceCommand = command;
            return Task.FromResult(Accepted());
        }

        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(CreateServiceRevisionCommand command, CancellationToken ct = default)
        {
            CreateRevisionCommand = command;
            return Task.FromResult(Accepted());
        }

        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(PrepareServiceRevisionCommand command, CancellationToken ct = default)
        {
            PrepareRevisionCommand = command;
            return Task.FromResult(Accepted());
        }

        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(PublishServiceRevisionCommand command, CancellationToken ct = default)
        {
            PublishRevisionCommand = command;
            return Task.FromResult(Accepted());
        }

        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(RetireServiceRevisionCommand command, CancellationToken ct = default) =>
            Task.FromResult(Accepted());

        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(SetDefaultServingRevisionCommand command, CancellationToken ct = default)
        {
            SetDefaultServingRevisionCommand = command;
            return Task.FromResult(Accepted());
        }

        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(ActivateServiceRevisionCommand command, CancellationToken ct = default)
        {
            ActivateServiceRevisionCommand = command;
            return Task.FromResult(Accepted());
        }

        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(DeactivateServiceDeploymentCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(ReplaceServiceServingTargetsCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(StartServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(AdvanceServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(PauseServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(ResumeServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(RollbackServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());

        private static ServiceCommandAcceptedReceipt Accepted() => new("target-actor", "cmd-1", "corr-1");
    }

    private sealed class FakeServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        public readonly Queue<ServiceCatalogSnapshot?> GetServiceResults = new();
        public IReadOnlyList<ServiceCatalogSnapshot> ListServicesResult { get; set; } = [];
        public ListRequest? LastListRequest { get; private set; }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            _ = identity;
            return Task.FromResult(GetServiceResults.Count > 0 ? GetServiceResults.Dequeue() : null);
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default)
        {
            LastListRequest = new ListRequest(tenantId, appId, @namespace, take);
            return Task.FromResult(ListServicesResult);
        }

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);

        public sealed record ListRequest(string TenantId, string AppId, string Namespace, int Take);
    }

    private sealed class FakeWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        public Dictionary<string, WorkflowActorBinding> Bindings { get; } = new(StringComparer.Ordinal);

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            Bindings.TryGetValue(actorId, out var binding);
            return Task.FromResult(binding);
        }
    }
}
