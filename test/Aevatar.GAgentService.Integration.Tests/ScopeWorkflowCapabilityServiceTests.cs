using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
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
        var governanceCommandPort = new FakeServiceGovernanceCommandPort();
        var queryPort = new FakeServiceLifecycleQueryPort();
        var governanceQueryPort = new FakeServiceGovernanceQueryPort();
        var admission = new RecordingWorkflowCapabilityAdmissionService();
        queryPort.GetServiceResults.Enqueue(null);
        var service = new ScopeWorkflowCommandApplicationService(
            commandPort,
            queryPort,
            governanceCommandPort,
            governanceQueryPort,
            Options.Create(options),
            admission);

        var request = new ScopeWorkflowUpsertRequest(
            "external-user-1",
            "approval-flow",
            "name: approval",
            WorkflowName: "approval",
            DisplayName: "Approval Flow",
            InlineWorkflowYamls: new Dictionary<string, string> { ["child.yaml"] = "name: child" },
            RevisionId: revisionId)
        {
            CapabilityAdmission = new WorkflowCapabilityAdmissionContext(
                callerId: "caller-alpha",
                nyxIdCallerCredential: NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                    "caller-bearer-alpha"),
                nyxIdOrganizationBearerToken: "organization-bearer-alpha",
                executionMode: ExternalCapabilityExecutionMode.Interactive),
        };

        var result = await service.UpsertAsync(request);

        result.ScopeId.Should().Be("external-user-1");
        result.WorkflowId.Should().Be("approval-flow");
        result.ExpectedActorId.Should().Be(expectedActorId);
        result.ExpectedDeploymentId.Should().Be(expectedDeploymentId);
        result.AcceptanceStage.Should().Be("accepted");
        result.PropagationStage.Should().Be("readmodel_propagating");
        result.ReadModelUrl.Should().Be("/api/scopes/external-user-1/workflows/approval-flow");
        result.CommandHandles.Select(x => x.Stage).Should().Equal(
            "create_service",
            "create_revision",
            "prepare_revision",
            "publish_revision",
            "set_default_serving_revision",
            "activate_service_revision");
        result.DefinitionActorIdPrefix.Should().Be(expectedActorPrefix);
        commandPort.CreateServiceCommand!.Spec.Identity.Should().BeEquivalentTo(identity);
        commandPort.CreateRevisionCommand!.Spec.WorkflowSpec.DefinitionActorId.Should().Be(expectedActorPrefix);
        commandPort.CreateRevisionCommand.Spec.WorkflowSpec.CapabilityAdmissionPlan.Should()
            .BeEquivalentTo(admission.Plan);
        admission.Request.Should().NotBeNull();
        admission.Request!.WorkflowYaml.Should().Be("name: approval");
        admission.Request.InlineWorkflowYamls.Should().ContainKey("child.yaml");
        admission.Request.Access.ScopeId.Should().Be("external-user-1");
        admission.Request.Access.CallerId.Should().Be("caller-alpha");
        admission.Request.Access.NyxIdCallerCredential?.SourceReadableUserBearerToken
            .Should().Be("caller-bearer-alpha");
        admission.Request.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        governanceCommandPort.CreateEndpointCatalogCommand.Should().NotBeNull();
        governanceCommandPort.CreateEndpointCatalogCommand!.Spec.Identity.Should().BeEquivalentTo(identity);
        governanceCommandPort.CreateEndpointCatalogCommand.Spec.Endpoints.Should().ContainSingle(x => x.EndpointId == "chat");
    }

    [Fact]
    public async Task UpsertAsync_WhenCapabilityAdmissionFails_ShouldDispatchNoMutation()
    {
        var commandPort = new FakeServiceCommandPort();
        var governanceCommandPort = new FakeServiceGovernanceCommandPort();
        var admission = new RecordingWorkflowCapabilityAdmissionService
        {
            Exception = new WorkflowExternalCapabilityAdmissionException(new ExternalCapabilityReadiness
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                Status = ExternalCapabilityReadinessStatus.CredentialConnectionRequired,
                Blockers =
                {
                    new ExternalCapabilityBlocker
                    {
                        Status = ExternalCapabilityReadinessStatus.CredentialConnectionRequired,
                        Code = "CREDENTIAL_CONNECTION_REQUIRED",
                        SafeMessage = "Connect the selected credential before saving the workflow.",
                    },
                },
            }),
        };
        var service = new ScopeWorkflowCommandApplicationService(
            commandPort,
            new FakeServiceLifecycleQueryPort(),
            governanceCommandPort,
            new FakeServiceGovernanceQueryPort(),
            Options.Create(new ScopeWorkflowCapabilityOptions()),
            admission);

        var act = () => service.UpsertAsync(new ScopeWorkflowUpsertRequest(
            "external-user-1",
            "blocked-flow",
            "name: blocked"));

        await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        commandPort.MutationCount.Should().Be(0);
        governanceCommandPort.MutationCount.Should().Be(0);
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
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable);

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
        var updatedAt = DateTimeOffset.UtcNow;
        var serviceKey = ServiceKeys.Build("external-user-3", options.ServiceAppId, options.ServiceNamespace, "approval-flow");
        var serviceSnapshot = new ServiceCatalogSnapshot(
            serviceKey,
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
            updatedAt);
        var queryPort = new FakeServiceLifecycleQueryPort
        {
            ListServicesResult = [serviceSnapshot],
            DeploymentCatalogResult = new ServiceDeploymentCatalogSnapshot(
                serviceKey,
                [new ServiceDeploymentSnapshot("dep-1", "rev-1", definitionActorId, "active", updatedAt, updatedAt)],
                updatedAt),
        };
        queryPort.GetServiceResults.Enqueue(queryPort.ListServicesResult[0]);
        var bindingReader = new FakeWorkflowActorBindingReader();
        bindingReader.Bindings[definitionActorId] = new WorkflowActorBinding(
            WorkflowActorKind.Definition,
            definitionActorId,
            definitionActorId,
            string.Empty,
            "approval",
            "name: approval",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable);
        bindingReader.Bindings[runActorId] = new WorkflowActorBinding(
            WorkflowActorKind.Run,
            runActorId,
            definitionActorId,
            "run-1",
            "approval",
            string.Empty,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable);

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

        public int MutationCount { get; private set; }

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(CreateServiceDefinitionCommand command, CancellationToken ct = default)
        {
            MutationCount++;
            CreateServiceCommand = command;
            return Task.FromResult(Accepted());
        }

        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(UpdateServiceDefinitionCommand command, CancellationToken ct = default)
        {
            MutationCount++;
            UpdateServiceCommand = command;
            return Task.FromResult(Accepted());
        }

        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(CreateServiceRevisionCommand command, CancellationToken ct = default)
        {
            MutationCount++;
            CreateRevisionCommand = command;
            return Task.FromResult(Accepted());
        }

        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(PrepareServiceRevisionCommand command, CancellationToken ct = default)
        {
            MutationCount++;
            PrepareRevisionCommand = command;
            return Task.FromResult(Accepted());
        }

        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(PublishServiceRevisionCommand command, CancellationToken ct = default)
        {
            MutationCount++;
            PublishRevisionCommand = command;
            return Task.FromResult(Accepted());
        }

        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(RetireServiceRevisionCommand command, CancellationToken ct = default) =>
            Task.FromResult(Accepted());

        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(SetDefaultServingRevisionCommand command, CancellationToken ct = default)
        {
            MutationCount++;
            SetDefaultServingRevisionCommand = command;
            return Task.FromResult(Accepted());
        }

        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(ActivateServiceRevisionCommand command, CancellationToken ct = default)
        {
            MutationCount++;
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
        public ServiceDeploymentCatalogSnapshot? DeploymentCatalogResult { get; set; }
        public ListRequest? LastListRequest { get; private set; }
        private ServiceCatalogSnapshot? _lastServiceSnapshot;

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            _lastServiceSnapshot = GetServiceResults.Count > 0
                ? GetServiceResults.Dequeue()
                : ListServicesResult.FirstOrDefault(x => string.Equals(x.ServiceId, identity.ServiceId, StringComparison.Ordinal));
            return Task.FromResult(_lastServiceSnapshot);
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default)
        {
            LastListRequest = new ListRequest(tenantId, appId, @namespace, take);
            return Task.FromResult(ListServicesResult);
        }

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            if (DeploymentCatalogResult != null)
                return Task.FromResult<ServiceDeploymentCatalogSnapshot?>(DeploymentCatalogResult);

            var serviceKey = ServiceKeys.Build(identity);
            var service = ListServicesResult.FirstOrDefault(x => string.Equals(x.ServiceKey, serviceKey, StringComparison.Ordinal))
                ?? _lastServiceSnapshot;
            if (service == null || string.IsNullOrWhiteSpace(service.DeploymentId))
                return Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);

            return Task.FromResult<ServiceDeploymentCatalogSnapshot?>(new ServiceDeploymentCatalogSnapshot(
                service.ServiceKey,
                [new ServiceDeploymentSnapshot(
                    service.DeploymentId,
                    service.ActiveServingRevisionId,
                    service.PrimaryActorId,
                    service.DeploymentStatus,
                    service.UpdatedAt,
                    service.UpdatedAt)],
                service.UpdatedAt));
        }

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

    private sealed class FakeServiceGovernanceCommandPort : IServiceGovernanceCommandPort
    {
        private static readonly ServiceCommandAcceptedReceipt DefaultReceipt =
            new("governance-actor", "cmd-governance", "corr-governance");

        public CreateServiceEndpointCatalogCommand? CreateEndpointCatalogCommand { get; private set; }

        public int MutationCount { get; private set; }

        public Task<ServiceCommandAcceptedReceipt> CreateBindingAsync(CreateServiceBindingCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> UpdateBindingAsync(UpdateServiceBindingCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> RetireBindingAsync(RetireServiceBindingCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> CreateEndpointCatalogAsync(CreateServiceEndpointCatalogCommand command, CancellationToken ct = default)
        {
            MutationCount++;
            CreateEndpointCatalogCommand = command;
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> UpdateEndpointCatalogAsync(UpdateServiceEndpointCatalogCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> CreatePolicyAsync(CreateServicePolicyCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> UpdatePolicyAsync(UpdateServicePolicyCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> RetirePolicyAsync(RetireServicePolicyCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);
    }

    private sealed class RecordingWorkflowCapabilityAdmissionService : IWorkflowExternalCapabilityAdmissionService
    {
        public WorkflowExternalCapabilityAdmissionRequest? Request { get; private set; }

        public PersistedWorkflowCapabilityAdmissionRequest? PersistedRequest { get; private set; }

        public Exception? Exception { get; init; }

        public WorkflowCapabilityAdmissionPlan Plan { get; } =
            WorkflowCapabilityAdmissionPlanIntegrity.Create(
                "name: approval",
                new Dictionary<string, string> { ["child.yaml"] = "name: child" },
                ExternalCapabilityExecutionMode.Interactive,
                [],
                []);

        public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
            WorkflowExternalCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            if (Exception is not null)
                throw Exception;

            return Task.FromResult(Plan.Clone());
        }

        public Task<WorkflowCapabilityAdmissionPlan> RevalidatePersistedAsync(
            PersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            PersistedRequest = request;
            if (Exception is not null)
                throw Exception;

            return Task.FromResult(request.Plan.Clone());
        }
    }

    private sealed class FakeServiceGovernanceQueryPort : IServiceGovernanceQueryPort
    {
        public Task<ServiceBindingCatalogSnapshot?> GetBindingsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceBindingCatalogSnapshot?>(null);

        public Task<ServiceEndpointCatalogSnapshot?> GetEndpointCatalogAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceEndpointCatalogSnapshot?>(null);

        public Task<ServicePolicyCatalogSnapshot?> GetPoliciesAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServicePolicyCatalogSnapshot?>(null);
    }
}
