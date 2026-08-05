using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Bindings;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Core.Assemblers;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScopeBindingCommandApplicationServiceTests
{
    private const string ScopeId = "scope-a";
    private static readonly ScopeWorkflowCapabilityOptions DefaultOptions = new()
    {
        DefaultServiceId = "default",
        ServiceAppId = "default",
        ServiceNamespace = "default",
    };

    [Fact]
    public async Task UpsertAsync_ShouldCreateDefaultServiceAndLifecycle_WhenNewWorkflowBindingIsSubmitted()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var governanceCommandPort = new RecordingServiceGovernanceCommandPort();
        var governanceQueryPort = new FakeServiceGovernanceQueryPort();
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var admission = new RecordingWorkflowCapabilityAdmissionService();
        var service = CreateService(
            commandPort,
            lifecyclePort,
            governanceCommandPort,
            governanceQueryPort,
            scopeScriptQueryPort,
            scriptDefinitionSnapshotPort,
            actorPort,
            capabilityAdmissionService: admission);

        var result = await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec(
                "workflow-stable-id",
                [
                    "name: main_runtime\nsteps:\n  - run: echo hello",
                    "name: child\nsteps:\n  - run: echo child",
                ])));

        commandPort.Calls.Should().HaveCount(6);
        commandPort.Calls[0].Method.Should().Be("CreateServiceAsync");
        commandPort.Calls[1].Method.Should().Be("CreateRevisionAsync");
        commandPort.Calls[2].Method.Should().Be("PrepareRevisionAsync");
        commandPort.Calls[3].Method.Should().Be("PublishRevisionAsync");
        commandPort.Calls[4].Method.Should().Be("SetDefaultServingRevisionAsync");
        commandPort.Calls[5].Method.Should().Be("ActivateServiceRevisionAsync");
        result.ScopeId.Should().Be(ScopeId);
        result.ServiceId.Should().Be(DefaultOptions.DefaultServiceId);
        result.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Workflow);
        result.AcceptanceStage.Should().Be("accepted");
        result.PropagationStage.Should().Be("readmodel_propagating");
        result.Workflow.Should().NotBeNull();
        var expectedDefinitionActorIdPrefix = DefaultOptions.BuildDefinitionActorIdPrefix(ScopeId, "workflow-stable-id");
        result.Workflow!.WorkflowId.Should().Be("workflow-stable-id");
        result.Workflow!.WorkflowName.Should().Be("main_runtime");
        result.Workflow.DefinitionActorIdPrefix.Should().Be(expectedDefinitionActorIdPrefix);
        result.ExpectedActorId.Should().StartWith($"{expectedDefinitionActorIdPrefix}:");
        result.DisplayName.Should().Be("main_runtime");

        var revisionCommand = commandPort.Calls[1].Command.Should().BeOfType<CreateServiceRevisionCommand>().Subject;
        revisionCommand.Spec.WorkflowSpec.Should().NotBeNull();
        revisionCommand.Spec.WorkflowSpec!.DefinitionActorId.Should().Be(expectedDefinitionActorIdPrefix);
        revisionCommand.Spec.WorkflowSpec.CapabilityAdmissionPlan.Should().BeEquivalentTo(admission.Plan);
        revisionCommand.Spec.WorkflowSpec.ExpectedExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Interactive);
        admission.Request.Should().NotBeNull();
        admission.Request!.WorkflowYaml.Should().Contain("name: main_runtime");
        admission.Request.InlineWorkflowYamls.Should().ContainKey("child");
        admission.Request.Access.ScopeId.Should().Be(ScopeId);

        var createCommand = commandPort.Calls[0].Command.Should().BeOfType<CreateServiceDefinitionCommand>().Subject;
        createCommand.Spec.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = ScopeId,
            AppId = DefaultOptions.ServiceAppId,
            Namespace = DefaultOptions.ServiceNamespace,
            ServiceId = DefaultOptions.DefaultServiceId,
        });
        governanceCommandPort.CreateEndpointCatalogCommand.Should().NotBeNull();
        governanceCommandPort.CreateEndpointCatalogCommand!.Spec.Endpoints.Should().ContainSingle();
        governanceCommandPort.CreateEndpointCatalogCommand.Spec.Endpoints[0].EndpointId.Should().Be("chat");
        governanceCommandPort.CreateEndpointCatalogCommand.Spec.Endpoints[0].ExposureKind.Should().Be(ServiceEndpointExposureKind.Internal);
    }

    [Fact]
    public async Task UpsertAsync_WhenWorkflowCapabilityAdmissionFails_ShouldDispatchNoMutation()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var governanceCommandPort = new RecordingServiceGovernanceCommandPort();
        var admission = new RecordingWorkflowCapabilityAdmissionService
        {
            Exception = new WorkflowExternalCapabilityAdmissionException(new ExternalCapabilityReadiness
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                Status = ExternalCapabilityReadinessStatus.ServiceAccessDenied,
            }),
        };
        var service = CreateService(
            commandPort,
            lifecyclePort,
            governanceCommandPort,
            new FakeServiceGovernanceQueryPort(),
            new FakeScopeScriptQueryPort(),
            new FakeScriptDefinitionSnapshotPort(),
            new FakeWorkflowRunActorPort(),
            capabilityAdmissionService: admission);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: blocked\nsteps:\n  - run: echo blocked",
            ])));

        await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        commandPort.Calls.Should().BeEmpty();
        lifecyclePort.GetServiceCallCount.Should().Be(0);
        governanceCommandPort.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing", "NYXID_EXPLICIT_REQUEST_GRANT_REQUIRED")]
    [InlineData("stale_digest", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_DIGEST_MISMATCH")]
    [InlineData("stale_risk", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_RISK_MISMATCH")]
    [InlineData("unknown_call_site", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH")]
    [InlineData("duplicate", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH")]
    public async Task UpsertAsync_WhenExplicitRequestConfirmationIsInvalid_ShouldDispatchNoMutation(
        string scenario,
        string expectedBlockerCode)
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var governanceCommandPort = new RecordingServiceGovernanceCommandPort();
        var service = CreateService(
            commandPort,
            lifecyclePort,
            governanceCommandPort,
            new FakeServiceGovernanceQueryPort(),
            new FakeScopeScriptQueryPort(),
            new FakeScriptDefinitionSnapshotPort(),
            new FakeWorkflowRunActorPort(),
            capabilityAdmissionService: ScopeExplicitRequestAdmissionTestFixture.CreateAdmissionService());
        var request = new ScopeBindingUpsertRequest(
            ScopeExplicitRequestAdmissionTestFixture.ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec(
                ScopeExplicitRequestAdmissionTestFixture.WorkflowId,
                [ScopeExplicitRequestAdmissionTestFixture.WorkflowYaml]),
            RevisionId: ScopeExplicitRequestAdmissionTestFixture.RevisionId,
            ServiceId: ScopeExplicitRequestAdmissionTestFixture.ServiceId)
        {
            CapabilityAdmission = ScopeExplicitRequestAdmissionTestFixture.CreateContext(scenario),
        };

        Func<Task> act = async () => await service.UpsertAsync(request);

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be(expectedBlockerCode);
        commandPort.Calls.Should().BeEmpty();
        lifecyclePort.GetServiceCallCount.Should().Be(0);
        governanceCommandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_WhenExplicitRequestConfirmationMatches_ShouldPersistCallerOwnedGrant()
    {
        var commandPort = new RecordingServiceCommandPort();
        var service = CreateService(
            commandPort,
            new FakeServiceLifecycleQueryPort(getResult: null),
            new RecordingServiceGovernanceCommandPort(),
            new FakeServiceGovernanceQueryPort(),
            new FakeScopeScriptQueryPort(),
            new FakeScriptDefinitionSnapshotPort(),
            new FakeWorkflowRunActorPort(),
            capabilityAdmissionService: ScopeExplicitRequestAdmissionTestFixture.CreateAdmissionService());

        var result = await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeExplicitRequestAdmissionTestFixture.ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec(
                ScopeExplicitRequestAdmissionTestFixture.WorkflowId,
                [ScopeExplicitRequestAdmissionTestFixture.WorkflowYaml]),
            RevisionId: ScopeExplicitRequestAdmissionTestFixture.RevisionId,
            ServiceId: ScopeExplicitRequestAdmissionTestFixture.ServiceId)
        {
            CapabilityAdmission = ScopeExplicitRequestAdmissionTestFixture.CreateContext("matching"),
        });

        result.ScopeId.Should().Be(ScopeExplicitRequestAdmissionTestFixture.ScopeId);
        result.Workflow!.WorkflowId.Should().Be(ScopeExplicitRequestAdmissionTestFixture.WorkflowId);
        result.ServiceId.Should().Be(ScopeExplicitRequestAdmissionTestFixture.ServiceId);
        result.RevisionId.Should().Be(ScopeExplicitRequestAdmissionTestFixture.RevisionId);
        var revision = commandPort.Calls
            .Single(call => call.Method == "CreateRevisionAsync")
            .Command.Should().BeOfType<CreateServiceRevisionCommand>().Subject;
        revision.Spec.WorkflowSpec.WorkflowId.Should().Be(ScopeExplicitRequestAdmissionTestFixture.WorkflowId);
        ScopeExplicitRequestAdmissionTestFixture.AssertCallerOwnedGrant(
            revision.Spec.WorkflowSpec.CapabilityAdmissionPlan);
    }

    [Fact]
    public async Task UpsertAsync_WithExistingPlanAndNoFreshConfirmation_ShouldRevalidateAndDispatch()
    {
        var existingPlan = await ScopeExplicitRequestAdmissionTestFixture.CreatePersistedPlanAsync(
            "scope_binding_upsert");
        var admission = new ScopeExplicitRequestAdmissionTestFixture.DelegatingAdmissionService(
            ScopeExplicitRequestAdmissionTestFixture.CreateAdmissionService());
        var commandPort = new RecordingServiceCommandPort();
        var service = CreateService(
            commandPort,
            new FakeServiceLifecycleQueryPort(getResult: null),
            new RecordingServiceGovernanceCommandPort(),
            new FakeServiceGovernanceQueryPort(),
            new FakeScopeScriptQueryPort(),
            new FakeScriptDefinitionSnapshotPort(),
            new FakeWorkflowRunActorPort(),
            capabilityAdmissionService: admission);

        var result = await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeExplicitRequestAdmissionTestFixture.ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec(
                ScopeExplicitRequestAdmissionTestFixture.WorkflowId,
                [ScopeExplicitRequestAdmissionTestFixture.WorkflowYaml]),
            RevisionId: ScopeExplicitRequestAdmissionTestFixture.RevisionId,
            ServiceId: ScopeExplicitRequestAdmissionTestFixture.ServiceId)
        {
            CapabilityAdmission = ScopeExplicitRequestAdmissionTestFixture.CreatePersistedContext(existingPlan),
        });

        result.RevisionId.Should().Be(ScopeExplicitRequestAdmissionTestFixture.RevisionId);
        admission.RevalidatePersistedCallCount.Should().Be(1);
        admission.AdmitCallCount.Should().Be(0);
        commandPort.Calls.Should().Contain(call => call.Method == "CreateRevisionAsync");
    }

    [Fact]
    public async Task UpsertAsync_ShouldNotInferWorkflowIdFromServiceId_WhenWorkflowIdIsOmitted()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var admission = new RecordingWorkflowCapabilityAdmissionService();
        var service = CreateService(
            commandPort,
            lifecyclePort,
            new FakeScopeScriptQueryPort(),
            new FakeScriptDefinitionSnapshotPort(),
            new FakeWorkflowRunActorPort(),
            capabilityAdmissionService: admission);

        var result = await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec(
            [
                "name: main_runtime\nsteps:\n  - run: echo hello",
            ]),
            ServiceId: "custom-service"));

        var expectedDefinitionActorIdPrefix = ScopeWorkflowCapabilityConventions.BuildDefaultDefinitionActorIdPrefix(DefaultOptions, ScopeId);
        result.Workflow.Should().NotBeNull();
        result.Workflow!.WorkflowId.Should().BeEmpty();
        result.Workflow.DefinitionActorIdPrefix.Should().Be(expectedDefinitionActorIdPrefix);
        result.ExpectedActorId.Should().StartWith($"{expectedDefinitionActorIdPrefix}:");

        var revisionCommand = commandPort.Calls[1].Command.Should().BeOfType<CreateServiceRevisionCommand>().Subject;
        revisionCommand.Spec.WorkflowSpec.Should().NotBeNull();
        revisionCommand.Spec.WorkflowSpec!.WorkflowId.Should().BeEmpty();
        revisionCommand.Spec.WorkflowSpec!.DefinitionActorId.Should().Be(expectedDefinitionActorIdPrefix);
        admission.Request.Should().NotBeNull();
        admission.Request!.WorkflowId.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldRecordExternalExposureIntent_WhenExposureIsDesired()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var externalExposureIntentPort = new RecordingExternalExposureIntentPort(commandPort);
        var service = CreateService(
            commandPort,
            lifecyclePort,
            new FakeScopeScriptQueryPort(),
            new FakeScriptDefinitionSnapshotPort(),
            new FakeWorkflowRunActorPort(),
            externalExposureIntentPort: externalExposureIntentPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ]),
            ExposureDesired: true));

        var createCommand = commandPort.Calls[0].Command.Should().BeOfType<CreateServiceDefinitionCommand>().Subject;
        createCommand.Spec.ExternalExposure.Should().NotBeNull();
        createCommand.Spec.ExternalExposure!.ExposureDesired.Should().BeTrue();
        externalExposureIntentPort.Requests.Should().ContainSingle();
        var request = externalExposureIntentPort.Requests[0];
        request.ExposureDesired.Should().BeTrue();
        request.DesiredDefinition.Should().NotBeNull();
        request.DesiredDefinition!.ExternalExposure.Should().NotBeNull();
        request.DesiredDefinition.ExternalExposure!.ExposureDesired.Should().BeTrue();
        commandPort.Calls.TakeLast(2).Select(call => call.Method)
            .Should()
            .Equal("ActivateServiceRevisionAsync", "ExternalExposureIntent");
    }

    [Fact]
    public async Task UpsertAsync_ShouldDispatchExposureIntentAfterAlreadyActiveReplay()
    {
        const string revisionId = "rev-platform-bind-1";
        const string workflowYaml = "name: main\nsteps:\n  - run: echo hello";
        var commandPort = new RecordingServiceCommandPort();
        var externalExposureIntentPort = new RecordingExternalExposureIntentPort(commandPort);
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.NotRequiredNoExternalService,
        };
        var capabilityAdmissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            inlineWorkflowYamls: null,
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);
        var existingHash = CreateWorkflowArtifactHash(
            revisionId,
            "main",
            workflowYaml,
            dependencies: dependencies,
            capabilityAdmissionPlan: capabilityAdmissionPlan);
        var existingService = new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            ScopeId,
            DefaultOptions.ServiceAppId,
            DefaultOptions.ServiceNamespace,
            DefaultOptions.DefaultServiceId,
            "main",
            revisionId,
            revisionId,
            "dep-1",
            "actor-1",
            "Active",
            [
                new ServiceEndpointSnapshot(
                    "chat",
                    "chat",
                    ServiceEndpointKind.Chat.ToString(),
                    GetTypeUrl(ChatRequestEvent.Descriptor),
                    GetTypeUrl(ChatResponseEvent.Descriptor),
                    "Workflow chat endpoint."),
            ],
            [],
            DateTimeOffset.UtcNow);
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            existingService,
            new ServiceRevisionCatalogSnapshot(
                "scope-a:default:default:default",
                [
                    new ServiceRevisionSnapshot(
                        revisionId,
                        ServiceImplementationKind.Workflow.ToString(),
                        ServiceRevisionStatus.Published.ToString(),
                        existingHash,
                        string.Empty,
                        [],
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        null),
                ],
                DateTimeOffset.UtcNow));
        var actorPort = new FakeWorkflowRunActorPort
        {
            ParseResultsByYaml =
            {
                [workflowYaml] = WorkflowYamlParseResult.Success("main", dependencies),
            },
        };
        var service = CreateService(
            commandPort,
            lifecyclePort,
            new FakeScopeScriptQueryPort(),
            new FakeScriptDefinitionSnapshotPort(),
            actorPort,
            externalExposureIntentPort: externalExposureIntentPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                workflowYaml,
            ]),
            RevisionId: revisionId,
            AllowExistingRevisionReplay: true,
            ReplayRevisionId: revisionId,
            ExposureDesired: true));

        commandPort.Calls.Should().ContainSingle(call => call.Method == "UpdateServiceAsync");
        commandPort.Calls.Should().NotContain(call => call.Method == "CreateRevisionAsync");
        externalExposureIntentPort.Requests.Should().ContainSingle();
        var request = externalExposureIntentPort.Requests[0];
        request.ExposureDesired.Should().BeTrue();
        request.Identity.ServiceId.Should().Be(DefaultOptions.DefaultServiceId);
        request.DesiredDefinition.Should().NotBeNull();
        request.DesiredDefinition!.ExternalExposure.Should().NotBeNull();
        request.DesiredDefinition.ExternalExposure!.ExposureDesired.Should().BeTrue();
        request.ExistingService.Should().BeSameAs(existingService);
        commandPort.Calls.TakeLast(2).Select(call => call.Method)
            .Should()
            .Equal("ActivateServiceRevisionAsync", "ExternalExposureIntent");
    }

    [Fact]
    public async Task UpsertAsync_ShouldRetireExternalExposure_WhenExposureIsExplicitlyDisabled()
    {
        var commandPort = new RecordingServiceCommandPort();
        var externalExposureIntentPort = new RecordingExternalExposureIntentPort(commandPort);
        var lifecyclePort = new FakeServiceLifecycleQueryPort(new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            ScopeId,
            DefaultOptions.ServiceAppId,
            DefaultOptions.ServiceNamespace,
            DefaultOptions.DefaultServiceId,
            "main",
            "rev-old",
            "rev-old",
            "dep-old",
            "actor-old",
            "Active",
            [
                new ServiceEndpointSnapshot(
                    "chat",
                    "chat",
                    ServiceEndpointKind.Chat.ToString(),
                    GetTypeUrl(ChatRequestEvent.Descriptor),
                    GetTypeUrl(ChatResponseEvent.Descriptor),
                    "Default chat endpoint."),
            ],
            [],
            DateTimeOffset.UtcNow,
            new ServiceExternalExposureSnapshot(
                string.Empty,
                null,
                ServiceRegistrationStatus.Pending,
                DesiredSpecHash: "hash-1",
                ExposureDesired: true)));
        var service = CreateService(
            commandPort,
            lifecyclePort,
            new FakeScopeScriptQueryPort(),
            new FakeScriptDefinitionSnapshotPort(),
            new FakeWorkflowRunActorPort(),
            externalExposureIntentPort: externalExposureIntentPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ]),
            ExposureDesired: false));

        externalExposureIntentPort.Requests.Should().ContainSingle();
        var request = externalExposureIntentPort.Requests[0];
        request.ExposureDesired.Should().BeFalse();
        request.Identity.ServiceId.Should().Be(DefaultOptions.DefaultServiceId);
        request.ExistingService!.ExternalExposure!.DesiredSpecHash.Should().Be("hash-1");
    }

    [Fact]
    public async Task UpsertAsync_ShouldPreserveExternalExposure_WhenIntentIsOmitted()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            ScopeId,
            DefaultOptions.ServiceAppId,
            DefaultOptions.ServiceNamespace,
            DefaultOptions.DefaultServiceId,
            "main",
            "rev-old",
            "rev-old",
            "dep-old",
            "actor-old",
            "Active",
            [
                new ServiceEndpointSnapshot(
                    "chat",
                    "chat",
                    ServiceEndpointKind.Chat.ToString(),
                    GetTypeUrl(ChatRequestEvent.Descriptor),
                    GetTypeUrl(ChatResponseEvent.Descriptor),
                    "Default chat endpoint."),
            ],
            [],
            DateTimeOffset.UtcNow,
            new ServiceExternalExposureSnapshot(
                string.Empty,
                null,
                ServiceRegistrationStatus.Pending,
                DesiredSpecHash: "hash-1",
                ExposureDesired: true)));
        var service = CreateService(
            commandPort,
            lifecyclePort,
            new FakeScopeScriptQueryPort(),
            new FakeScriptDefinitionSnapshotPort(),
            new FakeWorkflowRunActorPort());

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ])));

        commandPort.Calls.Should().NotContain(call => call.Method == "RetireExternalExposureAsync");
        commandPort.Calls.Should().NotContain(call => call.Method == "UpdateServiceAsync");
    }

    [Fact]
    public async Task UpsertAsync_ShouldReturnAcceptedWithoutServingReadModelPolling()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var result = await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ])));

        commandPort.Calls.Should().HaveCount(6);
        lifecyclePort.GetServiceCallCount.Should().Be(1);
        result.AcceptanceStage.Should().Be("accepted");
        result.PropagationStage.Should().Be("readmodel_propagating");
    }

    [Fact]
    public void ScopeBindingCommandApplicationServiceSource_ShouldNotContainReadModelVisibilityWait()
    {
        var source = File.ReadAllText(GetProductionSourcePath());

        source.Should().NotContain(string.Concat("Task", ".Delay"));
        source.Should().NotContain("WaitForBindingVisibleAsync");
        source.Should().NotContain("ReadModelVisibility");
        source.Should().NotContain("IServiceServingQueryPort");
        source.Should().NotContain("GetServiceServingSetAsync");
    }

    [Fact]
    public async Task UpsertAsync_ShouldTreatFirstYamlAsEntryWorkflow_AndRemainingAsSubWorkflows()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: root_flow\nsteps:\n  - run: echo root",
                "name: sub_a\nsteps:\n  - run: echo a",
                "name: sub_b\nsteps:\n  - run: echo b",
            ]),
            DisplayName: "My App"));

        var revisionCommand = commandPort.Calls[1].Command.Should().BeOfType<CreateServiceRevisionCommand>().Subject;
        revisionCommand.Spec.Identity.ServiceId.Should().Be(DefaultOptions.DefaultServiceId);
        revisionCommand.Spec.WorkflowSpec.Should().NotBeNull();
        revisionCommand.Spec.WorkflowSpec!.WorkflowName.Should().Be("root_flow");
        revisionCommand.Spec.WorkflowSpec.WorkflowYaml.Should().Contain("name: root_flow");
        revisionCommand.Spec.WorkflowSpec.InlineWorkflowYamls.Should().ContainKey("sub_a");
        revisionCommand.Spec.WorkflowSpec.InlineWorkflowYamls.Should().ContainKey("sub_b");
        revisionCommand.Spec.WorkflowSpec.InlineWorkflowYamls.Should().NotContainKey("root_flow");
    }

    [Fact]
    public async Task UpsertAsync_ShouldIgnoreConfiguredServiceIdentityOverrides()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(
            commandPort,
            lifecyclePort,
            new RecordingServiceGovernanceCommandPort(),
            new FakeServiceGovernanceQueryPort(),
            scopeScriptQueryPort,
            scriptDefinitionSnapshotPort,
            actorPort,
            new ScopeWorkflowCapabilityOptions
            {
                DefaultServiceId = DefaultOptions.DefaultServiceId,
                ServiceAppId = "custom-app",
                ServiceNamespace = "custom-namespace",
            });

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ])));

        var createCommand = commandPort.Calls[0].Command.Should().BeOfType<CreateServiceDefinitionCommand>().Subject;
        createCommand.Spec.Identity.AppId.Should().Be(ScopeWorkflowCapabilityOptions.FixedServiceAppId);
        createCommand.Spec.Identity.Namespace.Should().Be(ScopeWorkflowCapabilityOptions.FixedServiceNamespace);
    }

    [Fact]
    public async Task UpsertAsync_ShouldCreateScriptingRevision_FromScopeScript()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort
        {
            Script = new ScopeScriptSummary(
                ScopeId,
                "script-a",
                "catalog-1",
                "definition-script-1",
                "script-rev-1",
                "hash-script-1",
                DateTimeOffset.UtcNow),
        };
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort
        {
            Snapshot = CreateScriptDefinitionSnapshot("script-a", "script-rev-1", "definition-script-1"),
        };
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var result = await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Scripting,
            Script: new ScopeBindingScriptSpec("script-a"),
            DisplayName: "Orders Script"));

        commandPort.Calls.Should().HaveCount(6);
        var revisionCommand = commandPort.Calls[1].Command.Should().BeOfType<CreateServiceRevisionCommand>().Subject;
        revisionCommand.Spec.ImplementationKind.Should().Be(ServiceImplementationKind.Scripting);
        revisionCommand.Spec.ScriptingSpec.Should().NotBeNull();
        revisionCommand.Spec.ScriptingSpec!.ScriptId.Should().Be("script-a");
        revisionCommand.Spec.ScriptingSpec.Revision.Should().Be("script-rev-1");
        revisionCommand.Spec.ScriptingSpec.DefinitionActorId.Should().Be("definition-script-1");
        result.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Scripting);
        result.Script.Should().NotBeNull();
        result.Script!.ScriptId.Should().Be("script-a");
        result.Script.ScriptRevision.Should().Be("script-rev-1");
    }

    [Fact]
    public async Task UpsertAsync_ShouldReuseExistingScriptingRevision_WhenExplicitRevisionAlreadyExists()
    {
        const string revisionId = "script-a-script-rev-1";
        var snapshot = CreateScriptDefinitionSnapshot("script-a", "script-rev-1", "definition-script-1");
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            new ServiceCatalogSnapshot(
                "scope-a:default:default:default",
                ScopeId,
                DefaultOptions.ServiceAppId,
                DefaultOptions.ServiceNamespace,
                DefaultOptions.DefaultServiceId,
                "Orders Script",
                revisionId,
                revisionId,
                "dep-1",
                "actor-1",
                "Active",
                [
                    new ServiceEndpointSnapshot(
                        "google.protobuf.StringValue",
                        "google.protobuf.StringValue",
                        ServiceEndpointKind.Command.ToString(),
                        "type.googleapis.com/google.protobuf.StringValue",
                        string.Empty,
                        "Scripting command endpoint for google.protobuf.StringValue."),
                ],
                [],
                DateTimeOffset.UtcNow),
            new ServiceRevisionCatalogSnapshot(
                "scope-a:default:default:default",
                [
                    new ServiceRevisionSnapshot(
                        revisionId,
                        ServiceImplementationKind.Scripting.ToString(),
                        ServiceRevisionStatus.Published.ToString(),
                        CreateScriptingArtifactHash(revisionId, snapshot),
                        string.Empty,
                        [],
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        null),
                ],
                DateTimeOffset.UtcNow));
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort
        {
            Script = new ScopeScriptSummary(
                ScopeId,
                "script-a",
                "catalog-1",
                "definition-script-1",
                "script-rev-1",
                "hash-script-1",
                DateTimeOffset.UtcNow),
        };
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort
        {
            Snapshot = snapshot,
        };
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var result = await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Scripting,
            Script: new ScopeBindingScriptSpec("script-a"),
            DisplayName: "Orders Script",
            RevisionId: revisionId));

        commandPort.Calls.Should().HaveCount(4);
        commandPort.Calls.Should().NotContain(call => call.Method == "CreateRevisionAsync");
        commandPort.Calls[0].Method.Should().Be("PrepareRevisionAsync");
        result.RevisionId.Should().Be(revisionId);
        result.Script.Should().NotBeNull();
        result.Script!.ScriptRevision.Should().Be("script-rev-1");
    }

    [Fact]
    public async Task UpsertAsync_ShouldRejectScriptingRevisionReuse_WhenExistingRevisionArtifactDiffers()
    {
        const string revisionId = "script-a-script-rev-1";
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            new ServiceCatalogSnapshot(
                "scope-a:default:default:default",
                ScopeId,
                DefaultOptions.ServiceAppId,
                DefaultOptions.ServiceNamespace,
                DefaultOptions.DefaultServiceId,
                "Orders Script",
                revisionId,
                revisionId,
                "dep-1",
                "actor-1",
                "Active",
                [
                    new ServiceEndpointSnapshot(
                        "google.protobuf.StringValue",
                        "google.protobuf.StringValue",
                        ServiceEndpointKind.Command.ToString(),
                        "type.googleapis.com/google.protobuf.StringValue",
                        string.Empty,
                        "Scripting command endpoint for google.protobuf.StringValue."),
                ],
                [],
                DateTimeOffset.UtcNow),
            new ServiceRevisionCatalogSnapshot(
                "scope-a:default:default:default",
                [
                    new ServiceRevisionSnapshot(
                        revisionId,
                        ServiceImplementationKind.Scripting.ToString(),
                        ServiceRevisionStatus.Published.ToString(),
                        "different-hash",
                        string.Empty,
                        [],
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        null),
                ],
                DateTimeOffset.UtcNow));
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort
        {
            Script = new ScopeScriptSummary(
                ScopeId,
                "script-a",
                "catalog-1",
                "definition-script-1",
                "script-rev-1",
                "hash-script-1",
                DateTimeOffset.UtcNow),
        };
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort
        {
            Snapshot = CreateScriptDefinitionSnapshot("script-a", "script-rev-1", "definition-script-1"),
        };
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Scripting,
            Script: new ScopeBindingScriptSpec("script-a"),
            DisplayName: "Orders Script",
            RevisionId: revisionId));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different scripting artifact*");
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldRejectScriptingBinding_WhenScriptSpecIsMissing()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Scripting));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*script is required*");
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldRejectScriptingBinding_WhenRequestedRevisionDiffersFromActiveRevision()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort
        {
            Script = new ScopeScriptSummary(
                ScopeId,
                "script-a",
                "catalog-1",
                "definition-script-1",
                "script-rev-1",
                "hash-script-1",
                DateTimeOffset.UtcNow),
        };
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Scripting,
            Script: new ScopeBindingScriptSpec("script-a", "script-rev-2")));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*currently at revision*");
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldRejectScriptingBinding_WhenScopeScriptIsMissing()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Scripting,
            Script: new ScopeBindingScriptSpec("script-missing")));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not have an active script*");
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldRejectScriptingBinding_WhenScriptDeclaresNoCommandEndpoints()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort
        {
            Script = new ScopeScriptSummary(
                ScopeId,
                "script-a",
                "catalog-1",
                "definition-script-1",
                "script-rev-1",
                "hash-script-1",
                DateTimeOffset.UtcNow),
        };
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort
        {
            Snapshot = CreateScriptDefinitionSnapshot(
                "script-a",
                "script-rev-1",
                "definition-script-1",
                ScriptMessageKind.InternalSignal),
        };
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Scripting,
            Script: new ScopeBindingScriptSpec("script-a")));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not declare command endpoints*");
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldCreateStaticRevision_ForGAgentBinding()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var result = await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
                ScopeBindingImplementationKind.GAgent,
                GAgent: new ScopeBindingGAgentSpec(
                    GAgentServiceTestKit.TestStaticServiceAgentKind,
                    [
                    new ScopeBindingGAgentEndpoint(
                        "run",
                        "Run",
                        ServiceEndpointKind.Command,
                        "type.googleapis.com/google.protobuf.StringValue",
                        string.Empty,
                        "Run the bound gagent."),
                    ]),
            DisplayName: "Orders GAgent"));

        commandPort.Calls.Should().HaveCount(6);
        var createCommand = commandPort.Calls[0].Command.Should().BeOfType<CreateServiceDefinitionCommand>().Subject;
        createCommand.Spec.Endpoints.Should().HaveCount(2);
        createCommand.Spec.Endpoints.Select(x => x.EndpointId).Should().Contain(["chat", "run"]);
        var revisionCommand = commandPort.Calls[1].Command.Should().BeOfType<CreateServiceRevisionCommand>().Subject;
        revisionCommand.Spec.ImplementationKind.Should().Be(ServiceImplementationKind.Static);
        revisionCommand.Spec.StaticSpec.Should().NotBeNull();
        revisionCommand.Spec.StaticSpec!.ActorTypeName.Should().Be(typeof(TestStaticServiceAgent).FullName);
        revisionCommand.Spec.StaticSpec.AgentKind.Should().Be(GAgentServiceTestKit.TestStaticServiceAgentKind);
        revisionCommand.Spec.StaticSpec.PreferredActorId.Should().BeEmpty();
        result.ImplementationKind.Should().Be(ScopeBindingImplementationKind.GAgent);
        result.GAgent.Should().NotBeNull();
        result.GAgent!.DiagnosticClrTypeName.Should().Be(typeof(TestStaticServiceAgent).FullName);
    }

    [Fact]
    public async Task UpsertAsync_ShouldRejectGAgentBinding_WhenAgentKindIsMissing()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);
        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.GAgent,
            GAgent: new ScopeBindingGAgentSpec(
                " ",
                [])));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*AgentKind*");
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldInsertDefaultChatEndpoint_WhenEndpointsAreMissing()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.GAgent,
            GAgent: new ScopeBindingGAgentSpec(
                GAgentServiceTestKit.TestStaticServiceAgentKind,
                [])));

        commandPort.Calls.Should().HaveCount(6);
        var createCommand = commandPort.Calls[0].Command.Should().BeOfType<CreateServiceDefinitionCommand>().Subject;
        createCommand.Spec.Endpoints.Should().ContainSingle();
        createCommand.Spec.Endpoints[0].EndpointId.Should().Be("chat");
    }

    [Fact]
    public async Task UpsertAsync_ShouldUpdateExistingService_WhenDisplayNameChanges()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            ScopeId,
            DefaultOptions.ServiceAppId,
            DefaultOptions.ServiceNamespace,
            DefaultOptions.DefaultServiceId,
            "Old Name",
            "rev-old",
            "rev-old",
            "dep-old",
            "actor-old",
            "Active",
            [],
            [],
            DateTimeOffset.UtcNow));
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ]),
            DisplayName: "Orders App"));

        commandPort.Calls.Should().HaveCount(6);
        commandPort.Calls[0].Method.Should().Be("UpdateServiceAsync");
        commandPort.Calls.Should().NotContain(call => call.Method == "CreateServiceAsync");
        var updateCommand = commandPort.Calls[0].Command.Should().BeOfType<UpdateServiceDefinitionCommand>().Subject;
        updateCommand.Spec.DisplayName.Should().Be("Orders App");
    }

    [Fact]
    public async Task UpsertAsync_ShouldPreserveExistingPolicyIds_WhenUpdatingServiceDefinitionForEndpointDrift()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            ScopeId,
            DefaultOptions.ServiceAppId,
            DefaultOptions.ServiceNamespace,
            DefaultOptions.DefaultServiceId,
            "main",
            "rev-old",
            "rev-old",
            "dep-old",
            "actor-old",
            "Active",
            [
                new ServiceEndpointSnapshot(
                    "chat",
                    "chat",
                    ServiceEndpointKind.Command.ToString(),
                    "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                    "type.googleapis.com/aevatar.ai.ChatResponseEvent",
                    "Old workflow endpoint contract."),
            ],
            ["policy-a", "policy-b"],
            DateTimeOffset.UtcNow));
        var governanceCommandPort = new RecordingServiceGovernanceCommandPort();
        var governanceQueryPort = new FakeServiceGovernanceQueryPort
        {
            EndpointCatalog = new ServiceEndpointCatalogSnapshot(
                "scope-a:default:default:default",
                [
                    new ServiceEndpointExposureSnapshot(
                        "chat",
                        "chat",
                        ServiceEndpointKind.Chat,
                        "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                        "type.googleapis.com/aevatar.ai.ChatResponseEvent",
                        "Workflow chat endpoint.",
                        ServiceEndpointExposureKind.Public,
                        ["invoke-policy"]),
                ],
                DateTimeOffset.UtcNow),
        };
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, governanceCommandPort, governanceQueryPort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ]),
            DisplayName: "Orders App"));

        var updateCommand = commandPort.Calls[0].Command.Should().BeOfType<UpdateServiceDefinitionCommand>().Subject;
        updateCommand.Spec.PolicyIds.Should().Equal("policy-a", "policy-b");
        governanceCommandPort.UpdateEndpointCatalogCommand.Should().NotBeNull();
        governanceCommandPort.UpdateEndpointCatalogCommand!.Spec.Endpoints.Should().ContainSingle();
        governanceCommandPort.UpdateEndpointCatalogCommand.Spec.Endpoints[0].ExposureKind.Should().Be(ServiceEndpointExposureKind.Public);
        governanceCommandPort.UpdateEndpointCatalogCommand.Spec.Endpoints[0].PolicyIds.Should().Equal("invoke-policy");
    }

    [Fact]
    public async Task UpsertAsync_ShouldSkipServiceDefinitionMutation_WhenDisplayNameIsUnchanged()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            ScopeId,
            DefaultOptions.ServiceAppId,
            DefaultOptions.ServiceNamespace,
            DefaultOptions.DefaultServiceId,
            "main",
            "rev-old",
            "rev-old",
            "dep-old",
            "actor-old",
            "Active",
            [
                new ServiceEndpointSnapshot(
                    "chat",
                    "chat",
                    ServiceEndpointKind.Chat.ToString(),
                    "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                    "type.googleapis.com/aevatar.ai.ChatResponseEvent",
                    "Default chat endpoint."),
            ],
            [],
            DateTimeOffset.UtcNow));
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ])));

        commandPort.Calls.Should().HaveCount(5);
        commandPort.Calls.Should().NotContain(call =>
            string.Equals(call.Method, "CreateServiceAsync", StringComparison.Ordinal) ||
            string.Equals(call.Method, "UpdateServiceAsync", StringComparison.Ordinal));
        commandPort.Calls[0].Method.Should().Be("CreateRevisionAsync");
    }

    [Fact]
    public async Task UpsertAsync_ShouldHonorExplicitRevisionId()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var result = await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ]),
            DisplayName: "Orders App",
            RevisionId: "rev-explicit"));

        var revisionCommand = commandPort.Calls[1].Command.Should().BeOfType<CreateServiceRevisionCommand>().Subject;
        revisionCommand.Spec.RevisionId.Should().Be("rev-explicit");
        result.RevisionId.Should().Be("rev-explicit");
    }

    [Fact]
    public async Task UpsertAsync_ShouldRejectExistingWorkflowRevision_WhenReplayIsNotAllowed()
    {
        const string revisionId = "rev-platform-bind-1";
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            new ServiceCatalogSnapshot(
                "scope-a:default:default:default",
                ScopeId,
                DefaultOptions.ServiceAppId,
                DefaultOptions.ServiceNamespace,
                DefaultOptions.DefaultServiceId,
                "main",
                revisionId,
                revisionId,
                "dep-1",
                "actor-1",
                "Active",
                [
                    new ServiceEndpointSnapshot(
                        "chat",
                        "chat",
                        ServiceEndpointKind.Chat.ToString(),
                        "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                        "type.googleapis.com/aevatar.ai.ChatResponseEvent",
                        "Workflow chat endpoint."),
                ],
                [],
                DateTimeOffset.UtcNow),
            new ServiceRevisionCatalogSnapshot(
                "scope-a:default:default:default",
                [
                    new ServiceRevisionSnapshot(
                        revisionId,
                        ServiceImplementationKind.Workflow.ToString(),
                        ServiceRevisionStatus.Published.ToString(),
                        string.Empty,
                        string.Empty,
                        [],
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        null),
                ],
                DateTimeOffset.UtcNow));
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ]),
            RevisionId: revisionId));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
        commandPort.Calls.Should().ContainSingle(call => call.Method == "UpdateServiceAsync");
        commandPort.Calls.Should().NotContain(call => call.Method == "CreateRevisionAsync");
    }

    [Fact]
    public async Task UpsertAsync_ShouldReuseExistingWorkflowRevision_WhenReplayRevisionMatchesAndArtifactHashMatches()
    {
        const string revisionId = "rev-platform-bind-1";
        const string workflowYaml = "name: main\nsteps:\n  - run: echo hello";
        var commandPort = new RecordingServiceCommandPort();
        var dependencies = new WorkflowAuthorizationDependencies
        {
            OwnerLlmRouteRequired = false,
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.NotRequiredNoExternalService,
        };
        var capabilityAdmissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            inlineWorkflowYamls: null,
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);
        var existingHash = CreateWorkflowArtifactHash(
            revisionId,
            "main",
            workflowYaml,
            dependencies: dependencies,
            capabilityAdmissionPlan: capabilityAdmissionPlan);
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            new ServiceCatalogSnapshot(
                "scope-a:default:default:default",
                ScopeId,
                DefaultOptions.ServiceAppId,
                DefaultOptions.ServiceNamespace,
                DefaultOptions.DefaultServiceId,
                "main",
                revisionId,
                revisionId,
                "dep-1",
                "actor-1",
                "Active",
                [
                    new ServiceEndpointSnapshot(
                        "chat",
                        "chat",
                        ServiceEndpointKind.Chat.ToString(),
                        "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                        "type.googleapis.com/aevatar.ai.ChatResponseEvent",
                        "Workflow chat endpoint."),
                ],
                [],
                DateTimeOffset.UtcNow),
            new ServiceRevisionCatalogSnapshot(
                "scope-a:default:default:default",
                [
                    new ServiceRevisionSnapshot(
                        revisionId,
                        ServiceImplementationKind.Workflow.ToString(),
                        ServiceRevisionStatus.Published.ToString(),
                        existingHash,
                        string.Empty,
                        [],
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        null),
                ],
                DateTimeOffset.UtcNow));
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort
        {
            ParseResultsByYaml =
            {
                [workflowYaml] = WorkflowYamlParseResult.Success("main", dependencies),
            },
        };
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                workflowYaml,
            ]),
            RevisionId: revisionId,
            AllowExistingRevisionReplay: true,
            ReplayRevisionId: revisionId));

        var result = await act();

        result.RevisionId.Should().Be(revisionId);
        result.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Workflow);
        commandPort.Calls.Should().ContainSingle(call => call.Method == "UpdateServiceAsync");
        commandPort.Calls.Should().NotContain(call => call.Method == "CreateRevisionAsync");
        commandPort.Calls.Should().Contain(call => call.Method == "PrepareRevisionAsync");
        commandPort.Calls.Should().Contain(call => call.Method == "PublishRevisionAsync");
        commandPort.Calls.Should().Contain(call => call.Method == "SetDefaultServingRevisionAsync");
        commandPort.Calls.Should().Contain(call => call.Method == "ActivateServiceRevisionAsync");
    }

    [Fact]
    public async Task UpsertAsync_ShouldReplayExistingWorkflowRevision_WhenRevisionIsUnprepared()
    {
        const string revisionId = "rev-platform-bind-1";
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            new ServiceCatalogSnapshot(
                "scope-a:default:default:default",
                ScopeId,
                DefaultOptions.ServiceAppId,
                DefaultOptions.ServiceNamespace,
                DefaultOptions.DefaultServiceId,
                "main",
                revisionId,
                revisionId,
                "dep-1",
                "actor-1",
                "Active",
                [
                    new ServiceEndpointSnapshot(
                        "chat",
                        "chat",
                        ServiceEndpointKind.Chat.ToString(),
                        GetTypeUrl(ChatRequestEvent.Descriptor),
                        GetTypeUrl(ChatResponseEvent.Descriptor),
                        "Workflow chat endpoint."),
                ],
                [],
                DateTimeOffset.UtcNow),
            new ServiceRevisionCatalogSnapshot(
                "scope-a:default:default:default",
                [
                    new ServiceRevisionSnapshot(
                        revisionId,
                        ServiceImplementationKind.Workflow.ToString(),
                        ServiceRevisionStatus.Created.ToString(),
                        string.Empty,
                        string.Empty,
                        [],
                        DateTimeOffset.UtcNow.AddHours(-1),
                        null,
                        null,
                        null),
                ],
                DateTimeOffset.UtcNow));
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var result = await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ]),
            RevisionId: revisionId,
            AllowExistingRevisionReplay: true,
            ReplayRevisionId: revisionId));

        result.RevisionId.Should().Be(revisionId);
        commandPort.Calls.Should().NotContain(call => call.Method == "CreateRevisionAsync");
        commandPort.Calls.Should().Contain(call => call.Method == "PrepareRevisionAsync");
    }

    [Fact]
    public async Task UpsertAsync_ShouldRejectExistingWorkflowRevision_WhenReplayArtifactHashDoesNotMatch()
    {
        const string revisionId = "rev-platform-bind-1";
        var commandPort = new RecordingServiceCommandPort();
        var existingHash = CreateWorkflowArtifactHash(revisionId, "main", "name: main\nsteps:\n  - run: echo old");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            new ServiceCatalogSnapshot(
                "scope-a:default:default:default",
                ScopeId,
                DefaultOptions.ServiceAppId,
                DefaultOptions.ServiceNamespace,
                DefaultOptions.DefaultServiceId,
                "main",
                revisionId,
                revisionId,
                "dep-1",
                "actor-1",
                "Active",
                [
                    new ServiceEndpointSnapshot(
                        "chat",
                        "chat",
                        ServiceEndpointKind.Chat.ToString(),
                        "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                        "type.googleapis.com/aevatar.ai.ChatResponseEvent",
                        "Workflow chat endpoint."),
                ],
                [],
                DateTimeOffset.UtcNow),
            new ServiceRevisionCatalogSnapshot(
                "scope-a:default:default:default",
                [
                    new ServiceRevisionSnapshot(
                        revisionId,
                        ServiceImplementationKind.Workflow.ToString(),
                        ServiceRevisionStatus.Published.ToString(),
                        existingHash,
                        string.Empty,
                        [],
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        null),
                ],
                DateTimeOffset.UtcNow));
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ]),
            RevisionId: revisionId,
            AllowExistingRevisionReplay: true,
            ReplayRevisionId: revisionId));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different Workflow artifact*");
        commandPort.Calls.Should().ContainSingle(call => call.Method == "UpdateServiceAsync");
        commandPort.Calls.Should().NotContain(call => call.Method == "CreateRevisionAsync");
        commandPort.Calls.Should().NotContain(call => call.Method == "PrepareRevisionAsync");
    }

    [Fact]
    public async Task UpsertAsync_ShouldRejectExistingWorkflowRevision_WhenReplayRevisionDoesNotMatch()
    {
        const string revisionId = "rev-user-supplied";
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            new ServiceCatalogSnapshot(
                "scope-a:default:default:default",
                ScopeId,
                DefaultOptions.ServiceAppId,
                DefaultOptions.ServiceNamespace,
                DefaultOptions.DefaultServiceId,
                "main",
                revisionId,
                revisionId,
                "dep-1",
                "actor-1",
                "Active",
                [
                    new ServiceEndpointSnapshot(
                        "chat",
                        "chat",
                        ServiceEndpointKind.Chat.ToString(),
                        "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                        "type.googleapis.com/aevatar.ai.ChatResponseEvent",
                        "Workflow chat endpoint."),
                ],
                [],
                DateTimeOffset.UtcNow),
            new ServiceRevisionCatalogSnapshot(
                "scope-a:default:default:default",
                [
                    new ServiceRevisionSnapshot(
                        revisionId,
                        ServiceImplementationKind.Workflow.ToString(),
                        ServiceRevisionStatus.Published.ToString(),
                        string.Empty,
                        string.Empty,
                        [],
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        null),
                ],
                DateTimeOffset.UtcNow));
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ]),
            RevisionId: revisionId,
            AllowExistingRevisionReplay: true,
            ReplayRevisionId: "rev-other-command"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
        commandPort.Calls.Should().ContainSingle(call => call.Method == "UpdateServiceAsync");
        commandPort.Calls.Should().NotContain(call => call.Method == "CreateRevisionAsync");
        commandPort.Calls.Should().NotContain(call => call.Method == "PrepareRevisionAsync");
    }

    [Fact]
    public async Task UpsertAsync_ShouldReuseExistingGAgentRevision_WhenReplayRevisionMatchesAndArtifactHashMatches()
    {
        const string revisionId = "rev-static-bind-1";
        var commandPort = new RecordingServiceCommandPort();
        var existingHash = CreateStaticArtifactHash(revisionId, typeof(TestStaticServiceAgent).FullName!, [
            new ServiceEndpointDescriptor
            {
                EndpointId = "chat",
                DisplayName = "chat",
                Kind = ServiceEndpointKind.Chat,
                RequestTypeUrl = "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                ResponseTypeUrl = "type.googleapis.com/aevatar.ai.ChatResponseEvent",
                Description = "Default chat endpoint.",
            },
        ], serviceId: "default");
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            new ServiceCatalogSnapshot(
                "scope-a:default:default:default",
                ScopeId,
                DefaultOptions.ServiceAppId,
                DefaultOptions.ServiceNamespace,
                DefaultOptions.DefaultServiceId,
                "main",
                revisionId,
                revisionId,
                "dep-1",
                "actor-1",
                "Active",
                [
                    new ServiceEndpointSnapshot(
                        "chat",
                        "chat",
                        ServiceEndpointKind.Chat.ToString(),
                        "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                        "type.googleapis.com/aevatar.ai.ChatResponseEvent",
                        "Chat endpoint."),
                ],
                [],
                DateTimeOffset.UtcNow),
            new ServiceRevisionCatalogSnapshot(
                "scope-a:default:default:default",
                [
                    new ServiceRevisionSnapshot(
                        revisionId,
                        ServiceImplementationKind.Static.ToString(),
                        ServiceRevisionStatus.Published.ToString(),
                        existingHash,
                        string.Empty,
                        [],
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        null),
                ],
                DateTimeOffset.UtcNow));
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.GAgent,
            GAgent: new ScopeBindingGAgentSpec(
                GAgentServiceTestKit.TestStaticServiceAgentKind,
                []),
            RevisionId: revisionId,
            AllowExistingRevisionReplay: true,
            ReplayRevisionId: revisionId));

        var result = await act();

        result.RevisionId.Should().Be(revisionId);
        result.ImplementationKind.Should().Be(ScopeBindingImplementationKind.GAgent);
        commandPort.Calls.Should().ContainSingle(call => call.Method == "UpdateServiceAsync");
        commandPort.Calls.Should().NotContain(call => call.Method == "CreateRevisionAsync");
        commandPort.Calls.Should().Contain(call => call.Method == "PrepareRevisionAsync");
        commandPort.Calls.Should().Contain(call => call.Method == "PublishRevisionAsync");
        commandPort.Calls.Should().Contain(call => call.Method == "SetDefaultServingRevisionAsync");
        commandPort.Calls.Should().Contain(call => call.Method == "ActivateServiceRevisionAsync");
    }

    [Fact]
    public async Task UpsertAsync_ShouldThrow_WhenWorkflowNamesAreDuplicated()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: repeat\nsteps:\n  - run: echo root",
                "name: repeat\nsteps:\n  - run: echo child",
            ])));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Duplicate workflow name*");
    }

    [Fact]
    public async Task UpsertAsync_ShouldThrow_WhenImplementationKindIsUnsupported()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            (ScopeBindingImplementationKind)99,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ])));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unsupported implementationKind*");
    }

    [Fact]
    public async Task UpsertAsync_ShouldThrow_WhenWorkflowYamlEntryIsEmpty()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
                "   ",
            ])));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must not contain empty YAML entries*");
    }

    [Fact]
    public async Task UpsertAsync_ShouldThrow_WhenWorkflowYamlParsingFails()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        actorPort.ParseResultsByYaml["workflow: invalid"] = WorkflowYamlParseResult.Invalid("Workflow YAML is invalid.");
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "workflow: invalid",
            ])));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Workflow YAML is invalid.");
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldThrow_WhenParsedWorkflowNameIsBlank()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        actorPort.ParseResultsByYaml["name: blank"] = WorkflowYamlParseResult.Success(string.Empty);
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: blank",
            ])));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must define a workflow name*");
        commandPort.Calls.Should().BeEmpty();
    }

    // ── AppId routing tests ───────────────────────────────────────────────────

    [Fact]
    public async Task UpsertAsync_ShouldUseCustomAppId_WhenProvidedInWorkflowBindingRequest()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var governanceCommandPort = new RecordingServiceGovernanceCommandPort();
        var governanceQueryPort = new FakeServiceGovernanceQueryPort();
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, governanceCommandPort, governanceQueryPort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ]),
            AppId: "tenant-app"));

        var createCommand = commandPort.Calls[0].Command.Should().BeOfType<CreateServiceDefinitionCommand>().Subject;
        createCommand.Spec.Identity.AppId.Should().Be("tenant-app");
        createCommand.Spec.Identity.TenantId.Should().Be(ScopeId);
        createCommand.Spec.Identity.ServiceId.Should().Be(DefaultOptions.DefaultServiceId);
    }

    [Fact]
    public async Task UpsertAsync_ShouldUseCustomAppId_WhenProvidedInScriptingBindingRequest()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort
        {
            Script = new ScopeScriptSummary(
                ScopeId,
                "script-a",
                "catalog-1",
                "definition-script-1",
                "script-rev-1",
                "hash-script-1",
                DateTimeOffset.UtcNow),
        };
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort
        {
            Snapshot = CreateScriptDefinitionSnapshot("script-a", "script-rev-1", "definition-script-1"),
        };
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Scripting,
            Script: new ScopeBindingScriptSpec("script-a"),
            AppId: "tenant-app"));

        var createCommand = commandPort.Calls[0].Command.Should().BeOfType<CreateServiceDefinitionCommand>().Subject;
        createCommand.Spec.Identity.AppId.Should().Be("tenant-app");
    }

    [Fact]
    public async Task UpsertAsync_ShouldUseCustomAppId_WhenProvidedInGAgentBindingRequest()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.GAgent,
            GAgent: new ScopeBindingGAgentSpec(
                GAgentServiceTestKit.TestStaticServiceAgentKind,
                [
                    new ScopeBindingGAgentEndpoint(
                        "run",
                        "Run",
                        ServiceEndpointKind.Command,
                        "type.googleapis.com/google.protobuf.StringValue",
                        string.Empty,
                        "Run the bound gagent."),
                ]),
            AppId: "tenant-app"));

        var createCommand = commandPort.Calls[0].Command.Should().BeOfType<CreateServiceDefinitionCommand>().Subject;
        createCommand.Spec.Identity.AppId.Should().Be("tenant-app");
    }

    [Fact]
    public async Task UpsertAsync_ShouldFallbackToDefaultAppId_WhenAppIdIsNull()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var governanceCommandPort = new RecordingServiceGovernanceCommandPort();
        var governanceQueryPort = new FakeServiceGovernanceQueryPort();
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, governanceCommandPort, governanceQueryPort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ]),
            AppId: null));

        var createCommand = commandPort.Calls[0].Command.Should().BeOfType<CreateServiceDefinitionCommand>().Subject;
        createCommand.Spec.Identity.AppId.Should().Be(ScopeWorkflowCapabilityOptions.FixedServiceAppId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpsertAsync_ShouldFallbackToDefaultAppId_WhenAppIdIsBlank(string blankAppId)
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var governanceCommandPort = new RecordingServiceGovernanceCommandPort();
        var governanceQueryPort = new FakeServiceGovernanceQueryPort();
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, governanceCommandPort, governanceQueryPort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ]),
            AppId: blankAppId));

        var createCommand = commandPort.Calls[0].Command.Should().BeOfType<CreateServiceDefinitionCommand>().Subject;
        createCommand.Spec.Identity.AppId.Should().Be(ScopeWorkflowCapabilityOptions.FixedServiceAppId);
    }

    [Fact]
    public async Task UpsertAsync_ShouldNotPollServingReadiness()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var service = CreateService(
            commandPort,
            lifecyclePort,
            new RecordingServiceGovernanceCommandPort(),
            new FakeServiceGovernanceQueryPort(),
            new FakeScopeScriptQueryPort(),
            new FakeScriptDefinitionSnapshotPort(),
            new FakeWorkflowRunActorPort(),
            DefaultOptions);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ])));

        typeof(ScopeBindingCommandApplicationService)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Should()
            .NotContain(parameter => parameter.ParameterType == typeof(IServiceServingQueryPort));
    }

    private static ScopeBindingCommandApplicationService CreateService(
        RecordingServiceCommandPort commandPort,
        FakeServiceLifecycleQueryPort lifecyclePort,
        FakeScopeScriptQueryPort scopeScriptQueryPort,
        FakeScriptDefinitionSnapshotPort scriptDefinitionSnapshotPort,
        FakeWorkflowRunActorPort actorPort,
        IServiceExternalExposureIntentPort? externalExposureIntentPort = null,
        IWorkflowExternalCapabilityAdmissionService? capabilityAdmissionService = null) =>
        CreateService(
            commandPort,
            lifecyclePort,
            new RecordingServiceGovernanceCommandPort(),
            new FakeServiceGovernanceQueryPort(),
            scopeScriptQueryPort,
            scriptDefinitionSnapshotPort,
            actorPort,
            externalExposureIntentPort: externalExposureIntentPort,
            capabilityAdmissionService: capabilityAdmissionService);

    private static ScopeBindingCommandApplicationService CreateService(
        RecordingServiceCommandPort commandPort,
        FakeServiceLifecycleQueryPort lifecyclePort,
        RecordingServiceGovernanceCommandPort governanceCommandPort,
        FakeServiceGovernanceQueryPort governanceQueryPort,
        FakeScopeScriptQueryPort scopeScriptQueryPort,
        FakeScriptDefinitionSnapshotPort scriptDefinitionSnapshotPort,
        FakeWorkflowRunActorPort actorPort,
        IServiceExternalExposureIntentPort? externalExposureIntentPort = null,
        IWorkflowExternalCapabilityAdmissionService? capabilityAdmissionService = null) =>
        CreateService(
            commandPort,
            lifecyclePort,
            governanceCommandPort,
            governanceQueryPort,
            scopeScriptQueryPort,
            scriptDefinitionSnapshotPort,
            actorPort,
            DefaultOptions,
            externalExposureIntentPort,
            capabilityAdmissionService);

    private static ScopeBindingCommandApplicationService CreateService(
        RecordingServiceCommandPort commandPort,
        FakeServiceLifecycleQueryPort lifecyclePort,
        RecordingServiceGovernanceCommandPort governanceCommandPort,
        FakeServiceGovernanceQueryPort governanceQueryPort,
        FakeScopeScriptQueryPort scopeScriptQueryPort,
        FakeScriptDefinitionSnapshotPort scriptDefinitionSnapshotPort,
        FakeWorkflowRunActorPort actorPort,
        ScopeWorkflowCapabilityOptions options,
        IServiceExternalExposureIntentPort? externalExposureIntentPort = null,
        IWorkflowExternalCapabilityAdmissionService? capabilityAdmissionService = null) =>
        new(
            commandPort,
            lifecyclePort,
            governanceCommandPort,
            governanceQueryPort,
            scopeScriptQueryPort,
            scriptDefinitionSnapshotPort,
            actorPort,
            Options.Create(options),
            capabilityAdmissionService ?? new RecordingWorkflowCapabilityAdmissionService(),
            CreateStaticAgentKindRegistry(),
            externalExposureIntentPort);

    private static IAgentKindRegistry CreateStaticAgentKindRegistry()
    {
        var builder = new AgentKindRegistryBuilder();
        builder.Register<TestStaticServiceAgent>();
        return new AgentKindRegistry(builder.Build());
    }

    private static string GetProductionSourcePath(
        [System.Runtime.CompilerServices.CallerFilePath] string testFilePath = "")
    {
        var root = Directory.GetParent(testFilePath)?.Parent?.Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("Could not resolve repository root from test file path.");
        return Path.Combine(
            root,
            "src",
            "platform",
            "Aevatar.GAgentService.Application",
            "Bindings",
            "ScopeBindingCommandApplicationService.cs");
    }

    private static ScriptDefinitionSnapshot CreateScriptDefinitionSnapshot(
        string scriptId,
        string revision,
        string definitionActorId,
        ScriptMessageKind messageKind = ScriptMessageKind.Command) =>
        new(
            scriptId,
            revision,
            "return input;",
            "hash-script-1",
            "state",
            "readmodel",
            "v1",
            "hash-rm",
            RuntimeSemantics: new ScriptRuntimeSemanticsSpec
            {
                Messages =
                {
                    new ScriptMessageSemanticsSpec
                    {
                        TypeUrl = "type.googleapis.com/google.protobuf.StringValue",
                        DescriptorFullName = "google.protobuf.StringValue",
                        Kind = messageKind,
                    },
                },
            },
            DefinitionActorId: definitionActorId,
            ScopeId: ScopeId);

    private static string CreateScriptingArtifactHash(
        string revisionId,
        ScriptDefinitionSnapshot snapshot)
    {
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = new ServiceIdentity
            {
                TenantId = ScopeId,
                AppId = DefaultOptions.ServiceAppId,
                Namespace = DefaultOptions.ServiceNamespace,
                ServiceId = DefaultOptions.DefaultServiceId,
            },
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Scripting,
            ProtocolDescriptorSet = snapshot.ProtocolDescriptorSet,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                ScriptingPlan = new ScriptingServiceDeploymentPlan
                {
                    ScriptId = snapshot.ScriptId,
                    Revision = snapshot.Revision,
                    DefinitionActorId = snapshot.DefinitionActorId,
                    SourceHash = snapshot.SourceHash,
                    PackageSpec = ToServicePackage(snapshot.ScriptPackage),
                },
            },
        };
        artifact.Endpoints.Add(
            snapshot.RuntimeSemantics.Messages
                .Where(x => x.Kind == ScriptMessageKind.Command)
                .Select(x => new ServiceEndpointDescriptor
                {
                    EndpointId = string.IsNullOrWhiteSpace(x.DescriptorFullName)
                        ? x.TypeUrl ?? string.Empty
                        : x.DescriptorFullName,
                    DisplayName = string.IsNullOrWhiteSpace(x.DescriptorFullName)
                        ? x.TypeUrl ?? string.Empty
                        : x.DescriptorFullName,
                    Kind = ServiceEndpointKind.Command,
                    RequestTypeUrl = x.TypeUrl ?? string.Empty,
                    ResponseTypeUrl = string.Empty,
                    Description = $"Scripting command endpoint for {(string.IsNullOrWhiteSpace(x.DescriptorFullName) ? x.TypeUrl ?? string.Empty : x.DescriptorFullName)}.",
                }));
        return new PreparedServiceRevisionArtifactAssembler()
            .Assemble(artifact)
            .ArtifactHash;
    }

    private static string CreateWorkflowArtifactHash(
        string revisionId,
        string workflowName,
        string workflowYaml,
        string endpointDescription = "Workflow chat endpoint.",
        string? serviceId = null,
        WorkflowAuthorizationDependencies? dependencies = null,
        WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan = null)
    {
        var admittedCapabilities = capabilityAdmissionPlan?.ExternalCapabilities
            ?? dependencies?.ExternalCapabilities;
        var serviceGrantRequirement = capabilityAdmissionPlan is not null
            ? capabilityAdmissionPlan.ExternalCapabilities.Any(static capability =>
                capability.CapabilityCase ==
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService)
                ? Aevatar.GAgentService.Abstractions.Schedules.Authorization.AuthorizationGrantRequirement.Required
                : Aevatar.GAgentService.Abstractions.Schedules.Authorization.AuthorizationGrantRequirement.NotRequired
            : dependencies?.ServiceGrantPolicy switch
            {
                WorkflowServiceGrantPolicy.Required =>
                    Aevatar.GAgentService.Abstractions.Schedules.Authorization.AuthorizationGrantRequirement.Required,
                WorkflowServiceGrantPolicy.NotRequiredNoExternalService =>
                    Aevatar.GAgentService.Abstractions.Schedules.Authorization.AuthorizationGrantRequirement.NotRequired,
                _ => Aevatar.GAgentService.Abstractions.Schedules.Authorization.AuthorizationGrantRequirement.Unspecified,
            };
        var authorizationEvidence = new Aevatar.GAgentService.Abstractions.Schedules.Authorization.WorkflowRevisionAuthorizationEvidence
        {
            OwnerLlmRouteRequired = dependencies?.OwnerLlmRouteRequired ?? false,
            ServiceGrantRequirement = serviceGrantRequirement,
        };
        authorizationEvidence.ExternalCapabilities.Add(
            (admittedCapabilities ?? []).Select(static capability => capability.Clone()));
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = DefaultServiceIdentity(serviceId),
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            Endpoints =
            {
                new ServiceEndpointDescriptor
                {
                    EndpointId = "chat",
                    DisplayName = "chat",
                    Kind = ServiceEndpointKind.Chat,
                    RequestTypeUrl = GetTypeUrl(ChatRequestEvent.Descriptor),
                    ResponseTypeUrl = GetTypeUrl(ChatResponseEvent.Descriptor),
                    Description = endpointDescription,
                },
            },
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    WorkflowName = workflowName,
                    WorkflowYaml = workflowYaml,
                    DefinitionActorId = DefaultOptions.BuildDefinitionActorIdPrefix(
                        ScopeId,
                        DefaultOptions.DefaultServiceId),
                    AuthorizationEvidence = authorizationEvidence,
                    CapabilityAdmissionPlan = capabilityAdmissionPlan?.Clone(),
                    ExecutionMode = capabilityAdmissionPlan?.ExecutionMode ??
                                    ExternalCapabilityExecutionMode.Interactive,
                },
            },
        };
        return new PreparedServiceRevisionArtifactAssembler()
            .Assemble(artifact)
            .ArtifactHash;
    }

    private static string CreateStaticArtifactHash(
        string revisionId,
        string actorTypeName,
        IReadOnlyList<ServiceEndpointDescriptor> endpoints,
        string? serviceId = null)
    {
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = DefaultServiceIdentity(serviceId),
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Static,
            Endpoints = { endpoints.Select(x => x.Clone()) },
            DeploymentPlan = new ServiceDeploymentPlan
            {
                StaticPlan = new StaticServiceDeploymentPlan
                {
                    ActorTypeName = actorTypeName,
                    AgentKind = GAgentServiceTestKit.TestStaticServiceAgentKind,
                },
            },
        };
        return new PreparedServiceRevisionArtifactAssembler()
            .Assemble(artifact)
            .ArtifactHash;
    }

    private static string GetTypeUrl(Google.Protobuf.Reflection.MessageDescriptor descriptor) =>
        $"type.googleapis.com/{descriptor.FullName}";

    private static ServiceIdentity DefaultServiceIdentity(string? serviceId = null) =>
        new()
        {
            TenantId = ScopeId,
            AppId = DefaultOptions.ServiceAppId,
            Namespace = DefaultOptions.ServiceNamespace,
            ServiceId = serviceId ?? DefaultOptions.DefaultServiceId,
        };

    private static ServiceSourcePackageSpec ToServicePackage(ScriptPackageSpec packageSpec)
    {
        var result = new ServiceSourcePackageSpec
        {
            EntryBehaviorTypeName = packageSpec.EntryBehaviorTypeName ?? string.Empty,
            EntrySourcePath = packageSpec.EntrySourcePath ?? string.Empty,
        };
        result.CsharpSources.Add(packageSpec.CsharpSources.Select(x => new ServicePackageFile
        {
            Path = x.Path ?? string.Empty,
            Content = x.Content ?? string.Empty,
        }));
        result.ProtoFiles.Add(packageSpec.ProtoFiles.Select(x => new ServicePackageFile
        {
            Path = x.Path ?? string.Empty,
            Content = x.Content ?? string.Empty,
        }));
        return result;
    }

    private sealed record CommandCall(string Method, object? Command);

    private sealed class RecordingExternalExposureIntentPort(RecordingServiceCommandPort commandPort) : IServiceExternalExposureIntentPort
    {
        public List<ServiceExternalExposureIntentRequest> Requests { get; } = [];

        public Task ApplyAsync(ServiceExternalExposureIntentRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            commandPort.Calls.Add(new CommandCall("ExternalExposureIntent", request));
            return Task.CompletedTask;
        }
    }

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

        public Task<ServiceCommandAcceptedReceipt> ReconcileExternalExposureAsync(ReconcileExternalExposureCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("ReconcileExternalExposureAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> RetireExternalExposureAsync(RetireExternalExposureCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("RetireExternalExposureAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(DeactivateServiceDeploymentCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("DeactivateServiceDeploymentAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(ReplaceServiceServingTargetsCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("ReplaceServiceServingTargetsAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(StartServiceRolloutCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("StartServiceRolloutAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(AdvanceServiceRolloutCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("AdvanceServiceRolloutAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(PauseServiceRolloutCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("PauseServiceRolloutAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(ResumeServiceRolloutCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("ResumeServiceRolloutAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(RollbackServiceRolloutCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("RollbackServiceRolloutAsync", command));
            return Task.FromResult(DefaultReceipt);
        }
    }

    private sealed class FakeServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        private readonly ServiceCatalogSnapshot? _getResult;
        private readonly ServiceRevisionCatalogSnapshot? _revisions;

        public FakeServiceLifecycleQueryPort(
            ServiceCatalogSnapshot? getResult,
            ServiceRevisionCatalogSnapshot? revisions = null)
        {
            _getResult = getResult;
            _revisions = revisions;
        }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(RecordGetService(_getResult));

        public int GetServiceCallCount { get; private set; }

        private ServiceCatalogSnapshot? RecordGetService(ServiceCatalogSnapshot? snapshot)
        {
            GetServiceCallCount++;
            return snapshot;
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(_revisions);

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);
    }

    private sealed class RecordingServiceGovernanceCommandPort : IServiceGovernanceCommandPort
    {
        private static readonly ServiceCommandAcceptedReceipt DefaultReceipt =
            new("governance-actor", "cmd-governance", "correlation-governance");

        public CreateServiceEndpointCatalogCommand? CreateEndpointCatalogCommand { get; private set; }

        public UpdateServiceEndpointCatalogCommand? UpdateEndpointCatalogCommand { get; private set; }

        public List<CommandCall> Calls { get; } = [];

        public Task<ServiceCommandAcceptedReceipt> CreateBindingAsync(CreateServiceBindingCommand command, CancellationToken ct = default) =>
            Record(nameof(CreateBindingAsync), command);

        public Task<ServiceCommandAcceptedReceipt> UpdateBindingAsync(UpdateServiceBindingCommand command, CancellationToken ct = default) =>
            Record(nameof(UpdateBindingAsync), command);

        public Task<ServiceCommandAcceptedReceipt> RetireBindingAsync(RetireServiceBindingCommand command, CancellationToken ct = default) =>
            Record(nameof(RetireBindingAsync), command);

        public Task<ServiceCommandAcceptedReceipt> CreateEndpointCatalogAsync(CreateServiceEndpointCatalogCommand command, CancellationToken ct = default)
        {
            CreateEndpointCatalogCommand = command;
            return Record(nameof(CreateEndpointCatalogAsync), command);
        }

        public Task<ServiceCommandAcceptedReceipt> UpdateEndpointCatalogAsync(UpdateServiceEndpointCatalogCommand command, CancellationToken ct = default)
        {
            UpdateEndpointCatalogCommand = command;
            return Record(nameof(UpdateEndpointCatalogAsync), command);
        }

        public Task<ServiceCommandAcceptedReceipt> CreatePolicyAsync(CreateServicePolicyCommand command, CancellationToken ct = default) =>
            Record(nameof(CreatePolicyAsync), command);

        public Task<ServiceCommandAcceptedReceipt> UpdatePolicyAsync(UpdateServicePolicyCommand command, CancellationToken ct = default) =>
            Record(nameof(UpdatePolicyAsync), command);

        public Task<ServiceCommandAcceptedReceipt> RetirePolicyAsync(RetireServicePolicyCommand command, CancellationToken ct = default) =>
            Record(nameof(RetirePolicyAsync), command);

        private Task<ServiceCommandAcceptedReceipt> Record(string method, object command)
        {
            Calls.Add(new CommandCall(method, command));
            return Task.FromResult(DefaultReceipt);
        }
    }

    private sealed class FakeServiceGovernanceQueryPort : IServiceGovernanceQueryPort
    {
        public ServiceEndpointCatalogSnapshot? EndpointCatalog { get; set; }

        public Task<ServiceBindingCatalogSnapshot?> GetBindingsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceBindingCatalogSnapshot?>(null);

        public Task<ServiceEndpointCatalogSnapshot?> GetEndpointCatalogAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(EndpointCatalog);

        public Task<ServicePolicyCatalogSnapshot?> GetPoliciesAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServicePolicyCatalogSnapshot?>(null);
    }

    private sealed class FakeWorkflowRunActorPort : IWorkflowDefinitionProvisioningPort, IWorkflowRunProvisioningPort, IWorkflowDefinitionParser
    {
        public Dictionary<string, WorkflowYamlParseResult> ParseResultsByYaml { get; } =
            new(StringComparer.Ordinal);

        public Task<WorkflowDefinitionProvisioningReceipt> EnsureDefinitionAsync(WorkflowDefinitionBinding definition, string? preferredActorId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowRunCreationReceipt> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task BindWorkflowDefinitionAsync(
            string actorId,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
            string? scopeId,
            string? sourceKind,
            WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan,
            string? workflowId,
            string? revisionId,
            ExternalCapabilityExecutionMode expectedExecutionMode,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task MarkStoppedAsync(
            string actorId,
            string runId,
            string reason,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default)
        {
            if (ParseResultsByYaml.TryGetValue(workflowYaml, out var parseResult))
                return Task.FromResult(parseResult);

            var line = (workflowYaml ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(static value => value.StartsWith("name:", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(line))
                return Task.FromResult(WorkflowYamlParseResult.Invalid("Workflow YAML is invalid."));

            var workflowName = line["name:".Length..].Trim();
            return Task.FromResult(
                string.IsNullOrWhiteSpace(workflowName)
                    ? WorkflowYamlParseResult.Invalid("Workflow YAML is invalid.")
                    : WorkflowYamlParseResult.Success(
                        workflowName,
                        new WorkflowAuthorizationDependencies
                        {
                            ServiceGrantPolicy = WorkflowServiceGrantPolicy.NotRequiredNoExternalService,
                        }));
        }

        public async Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default)
        {
            if (inlineWorkflowDocuments.Count == 0)
                return WorkflowInlineYamlBundleParseResult.Invalid("workflowYamls is required.");

            var workflowYamlsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string entryWorkflowName = string.Empty;
            string entryWorkflowYaml = string.Empty;
            for (var i = 0; i < inlineWorkflowDocuments.Count; i++)
            {
                var document = inlineWorkflowDocuments[i];
                var parseResult = await ParseWorkflowYamlAsync(document.Yaml, ct);
                if (!parseResult.Succeeded)
                    return WorkflowInlineYamlBundleParseResult.Invalid(parseResult.Error, parseResult.ExternalCapabilityReadiness);

                if (!workflowYamlsByName.TryAdd(parseResult.WorkflowName, document.Yaml))
                    return WorkflowInlineYamlBundleParseResult.Invalid($"Duplicate workflow name '{parseResult.WorkflowName}' in workflowYamls.");

                if (i == 0)
                {
                    entryWorkflowName = parseResult.WorkflowName;
                    entryWorkflowYaml = document.Yaml;
                }
            }

            return WorkflowInlineYamlBundleParseResult.Success(entryWorkflowName, entryWorkflowYaml, workflowYamlsByName);
        }
    }

    private sealed class RecordingWorkflowCapabilityAdmissionService : IWorkflowExternalCapabilityAdmissionService
    {
        public WorkflowExternalCapabilityAdmissionRequest? Request { get; private set; }

        public PersistedWorkflowCapabilityAdmissionRequest? PersistedRequest { get; private set; }

        public WorkflowCapabilityAdmissionPlan? Plan { get; private set; }

        public Exception? Exception { get; init; }

        public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
            WorkflowExternalCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            if (Exception is not null)
                throw Exception;

            Plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
                request.WorkflowYaml,
                request.InlineWorkflowYamls,
                request.ExecutionMode,
                [],
                []);
            return Task.FromResult(Plan.Clone());
        }

        public Task<WorkflowCapabilityAdmissionPlan> RevalidatePersistedAsync(
            PersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            PersistedRequest = request;
            if (Exception is not null)
                throw Exception;

            Plan = request.Plan.Clone();
            return Task.FromResult(Plan.Clone());
        }
    }

    private sealed class FakeScopeScriptQueryPort : IScopeScriptQueryPort
    {
        public ScopeScriptSummary? Script { get; set; }

        public Task<IReadOnlyList<ScopeScriptSummary>> ListAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScopeScriptSummary>>(Script == null ? [] : [Script]);

        public Task<ScopeScriptSummary?> GetByScriptIdAsync(string scopeId, string scriptId, CancellationToken ct = default) =>
            Task.FromResult(
                Script != null &&
                string.Equals(Script.ScopeId, scopeId, StringComparison.Ordinal) &&
                string.Equals(Script.ScriptId, scriptId, StringComparison.Ordinal)
                    ? Script
                    : null);
    }

    private sealed class FakeScriptDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
    {
        public ScriptDefinitionSnapshot? Snapshot { get; set; }

        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            if (Snapshot == null ||
                !string.Equals(Snapshot.DefinitionActorId, definitionActorId, StringComparison.Ordinal) ||
                !string.Equals(Snapshot.Revision, requestedRevision, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Script definition snapshot was not found.");
            }

            return Task.FromResult(Snapshot);
        }
    }
}
