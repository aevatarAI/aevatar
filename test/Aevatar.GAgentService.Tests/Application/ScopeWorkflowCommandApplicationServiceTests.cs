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
using Aevatar.Workflow.Application.ExternalCapabilities;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScopeWorkflowCommandApplicationServiceTests
{
    private const string ScopeId = "test-scope";
    private const string WorkflowId = "my-workflow";
    private const string WorkflowYaml =
        "name: test\nsteps:\n  - id: hello\n    type: assign\n    parameters:\n      target: output\n      value: hello";
    private static readonly ScopeWorkflowCapabilityOptions DefaultOptions = new();

    [Fact]
    public async Task UpsertAsync_ShouldCreateServiceAndFullRevisionLifecycle_WhenNew()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var governanceCommandPort = new RecordingServiceGovernanceCommandPort();
        var governanceQueryPort = new FakeServiceGovernanceQueryPort();
        var service = CreateService(commandPort, lifecyclePort, governanceCommandPort, governanceQueryPort);

        var result = await service.UpsertAsync(new ScopeWorkflowUpsertRequest(
            ScopeId, WorkflowId, WorkflowYaml));

        commandPort.Calls.Should().HaveCount(5);
        commandPort.Calls[0].Method.Should().Be("CreateServiceAsync");
        commandPort.Calls[1].Method.Should().Be("CreateRevisionAsync");
        commandPort.Calls[2].Method.Should().Be("PrepareRevisionAsync");
        commandPort.Calls[3].Method.Should().Be("PublishRevisionAsync");
        commandPort.Calls[4].Method.Should().Be("ActivateServiceRevisionAsync");
        result.ScopeId.Should().Be(ScopeId);
        result.WorkflowId.Should().Be(WorkflowId);
        result.AcceptanceStage.Should().Be("accepted");
        result.PropagationStage.Should().Be("readmodel_propagating");
        result.ReadModelUrl.Should().Be($"/api/scopes/{ScopeId}/workflows/{WorkflowId}");
        result.CommandHandles.Select(x => x.Stage).Should().Equal(
            "create_service",
            "create_revision",
            "prepare_revision",
            "publish_revision",
            "activate_service_revision");

        var createCommand = commandPort.Calls[0].Command.Should().BeOfType<CreateServiceDefinitionCommand>().Subject;
        createCommand.Spec.Identity.TenantId.Should().Be(ScopeId);
        createCommand.Spec.Identity.AppId.Should().Be(DefaultOptions.ServiceAppId);
        createCommand.Spec.Identity.Namespace.Should().Be(DefaultOptions.ServiceNamespace);
        var revisionCommand = commandPort.Calls[1].Command.Should().BeOfType<CreateServiceRevisionCommand>().Subject;
        revisionCommand.Spec.WorkflowSpec.ExpectedExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Interactive);
        var prepareCommand = commandPort.Calls[2].Command
            .Should().BeOfType<PrepareServiceRevisionCommand>().Subject;
        var publishCommand = commandPort.Calls[3].Command
            .Should().BeOfType<PublishServiceRevisionCommand>().Subject;
        prepareCommand.PreparationSpec.Should().BeEquivalentTo(revisionCommand.Spec);
        publishCommand.PublicationSpec.Should().BeEquivalentTo(revisionCommand.Spec);
        var activateCommand = commandPort.Calls[4].Command
            .Should().BeOfType<ActivateServiceRevisionCommand>().Subject;
        activateCommand.ExpectedArtifactHash.Should().NotBeNullOrWhiteSpace();
        governanceCommandPort.CreateEndpointCatalogCommand.Should().NotBeNull();
        governanceCommandPort.CreateEndpointCatalogCommand!.Spec.Endpoints.Should().ContainSingle();
        governanceCommandPort.CreateEndpointCatalogCommand.Spec.Endpoints[0].EndpointId.Should().Be("chat");
        governanceCommandPort.CreateEndpointCatalogCommand.Spec.Endpoints[0].ExposureKind.Should().Be(ServiceEndpointExposureKind.Internal);
    }

    [Fact]
    public async Task UpsertAsync_ShouldFencePublishedEvidenceReplayToPersistedArtifact()
    {
        const string revisionId = "rev-published-evidence";
        var originalPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            WorkflowYaml,
            inlineWorkflowYamls: null,
            ExternalCapabilityExecutionMode.Interactive,
            [],
            [CreateSourceStamp(sourceVersion: 3)]);
        var refreshedPlan = originalPlan.Clone();
        refreshedPlan.SourceStamps.Clear();
        refreshedPlan.SourceStamps.Add(CreateSourceStamp(sourceVersion: 4));
        refreshedPlan.AdmissionDigest =
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(refreshedPlan);
        var identity = ScopeWorkflowCapabilityConventions.BuildIdentity(
            DefaultOptions,
            ScopeId,
            WorkflowId);
        var originalSpec = new ServiceRevisionSpec
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            WorkflowSpec = new WorkflowServiceRevisionSpec
            {
                ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                WorkflowId = WorkflowId,
                WorkflowName = "test",
                WorkflowYaml = WorkflowYaml,
                DefinitionActorId = DefaultOptions.BuildDefinitionActorIdPrefix(ScopeId, WorkflowId),
                CapabilityAdmissionPlan = originalPlan,
                ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            },
        };
        var workflow = new WorkflowParser().Parse(WorkflowYaml);
        var persistedArtifact = WorkflowServiceRevisionArtifactBuilder.Build(
            originalSpec,
            workflow.Name,
            WorkflowAuthorizationDependencyEvaluator.Evaluate(workflow),
            originalPlan);
        var normalizedArtifact = persistedArtifact.Clone();
        normalizedArtifact.ArtifactHash = string.Empty;
        persistedArtifact.ArtifactHash = Convert.ToHexString(
            SHA256.HashData(normalizedArtifact.ToByteArray()));
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null)
        {
            Revisions = new ServiceRevisionCatalogSnapshot(
                ServiceKeys.Build(identity),
                [
                    new ServiceRevisionSnapshot(
                        revisionId,
                        ServiceImplementationKind.Workflow.ToString(),
                        ServiceRevisionStatus.Published.ToString(),
                        persistedArtifact.ArtifactHash,
                        string.Empty,
                        [],
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        null,
                        null,
                        persistedArtifact),
                ],
                DateTimeOffset.UtcNow),
        };
        var commandPort = new RecordingServiceCommandPort();
        var service = CreateService(
            commandPort,
            lifecyclePort,
            new RecordingServiceGovernanceCommandPort(),
            new FakeServiceGovernanceQueryPort(),
            DefaultOptions,
            new FixedPlanAdmissionService(refreshedPlan));

        await service.UpsertAsync(new ScopeWorkflowUpsertRequest(
            ScopeId,
            WorkflowId,
            WorkflowYaml,
            WorkflowName: "test",
            RevisionId: revisionId)
        {
            CapabilityAdmission = new WorkflowCapabilityAdmissionContext(
                "caller-alpha",
                NyxIdCallerCredentialSelection.SourceReadableUserBearer("test-bearer"),
                executionMode: ExternalCapabilityExecutionMode.Interactive,
                existingPlan: originalPlan),
        });

        var activate = commandPort.Calls.Single(call =>
                call.Method == "ActivateServiceRevisionAsync")
            .Command.Should().BeOfType<ActivateServiceRevisionCommand>().Subject;
        activate.ExpectedArtifactHash.Should().Be(persistedArtifact.ArtifactHash);
        commandPort.Calls.Should().NotContain(call =>
            call.Method == "CreateRevisionAsync");
    }

    [Fact]
    public async Task UpsertAsync_ShouldUpdateService_WhenDisplayNameChanged()
    {
        var existingSnapshot = CreateServiceSnapshot(
            serviceId: WorkflowId,
            displayName: "Old Name");
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: existingSnapshot);
        var governanceCommandPort = new RecordingServiceGovernanceCommandPort();
        var governanceQueryPort = new FakeServiceGovernanceQueryPort
        {
            EndpointCatalog = new ServiceEndpointCatalogSnapshot(
                CreateServiceSnapshot(serviceId: WorkflowId, displayName: "Old Name").ServiceKey,
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
        var service = CreateService(commandPort, lifecyclePort, governanceCommandPort, governanceQueryPort);

        await service.UpsertAsync(new ScopeWorkflowUpsertRequest(
            ScopeId, WorkflowId, WorkflowYaml, DisplayName: "New Name"));

        commandPort.Calls.Should().Contain(c => c.Method == "UpdateServiceAsync");
        commandPort.Calls.Should().NotContain(c => c.Method == "CreateServiceAsync");
        governanceCommandPort.UpdateEndpointCatalogCommand.Should().NotBeNull();
        governanceCommandPort.UpdateEndpointCatalogCommand!.Spec.Endpoints.Should().ContainSingle();
        governanceCommandPort.UpdateEndpointCatalogCommand.Spec.Endpoints[0].ExposureKind.Should().Be(ServiceEndpointExposureKind.Public);
        governanceCommandPort.UpdateEndpointCatalogCommand.Spec.Endpoints[0].PolicyIds.Should().Equal("invoke-policy");
    }

    [Fact]
    public async Task UpsertAsync_ShouldIgnoreConfiguredServiceIdentityOverrides()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var service = CreateService(
            commandPort,
            lifecyclePort,
            new RecordingServiceGovernanceCommandPort(),
            new FakeServiceGovernanceQueryPort(),
            new ScopeWorkflowCapabilityOptions
            {
                ServiceAppId = "custom-app",
                ServiceNamespace = "custom-namespace",
            });

        await service.UpsertAsync(new ScopeWorkflowUpsertRequest(
            ScopeId, WorkflowId, WorkflowYaml));

        var createCommand = commandPort.Calls[0].Command.Should().BeOfType<CreateServiceDefinitionCommand>().Subject;
        createCommand.Spec.Identity.AppId.Should().Be(ScopeWorkflowCapabilityOptions.FixedServiceAppId);
        createCommand.Spec.Identity.Namespace.Should().Be(ScopeWorkflowCapabilityOptions.FixedServiceNamespace);
    }

    [Fact]
    public async Task UpsertAsync_ShouldReturnAcceptedOnlyHandles_WithoutWorkflowReadModelSummary()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var service = CreateService(commandPort, lifecyclePort);

        var result = await service.UpsertAsync(new ScopeWorkflowUpsertRequest(
            ScopeId, WorkflowId, WorkflowYaml));

        result.ScopeId.Should().Be(ScopeId);
        result.WorkflowId.Should().Be(WorkflowId);
        result.DisplayName.Should().Be(WorkflowId);
        result.ReadModelUrl.Should().Be($"/api/scopes/{ScopeId}/workflows/{WorkflowId}");
        result.CommandHandles.Should().HaveCount(5);
        result.CommandHandles.Should().OnlyContain(x => !string.IsNullOrWhiteSpace(x.CommandId));
    }

    [Fact]
    public async Task UpsertAsync_ShouldThrow_WhenWorkflowYamlIsEmpty()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var service = CreateService(commandPort, lifecyclePort);

        var act = () => service.UpsertAsync(new ScopeWorkflowUpsertRequest(
            ScopeId, WorkflowId, ""));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void WorkflowCapabilityAdmissionContext_ShouldCloneExplicitRequestConfirmations()
    {
        var confirmation = new NyxIdExplicitRequestConfirmation
        {
            CallSiteId = "wf-context/request-context",
            RequestContractDigest = "digest-context",
            AttestedRisk = NyxIdOperationRisk.ReadOnly,
        };
        var context = new WorkflowCapabilityAdmissionContext(
            "caller-context",
            null,
            null,
            ExternalCapabilityExecutionMode.Interactive,
            null,
            [confirmation]);
        confirmation.RequestContractDigest = "mutated-after-context-construction";

        var firstSnapshot = context.ExplicitRequestConfirmations;
        firstSnapshot.Should().ContainSingle().Which.RequestContractDigest.Should()
            .Be("digest-context");
        firstSnapshot[0].RequestContractDigest = "mutated-after-context-read";

        var secondSnapshot = context.ExplicitRequestConfirmations;
        secondSnapshot.Should().ContainSingle().Which.RequestContractDigest.Should()
            .Be("digest-context");
        secondSnapshot[0].Should().NotBeSameAs(firstSnapshot[0]);
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
        var governanceCommandPort = new RecordingServiceGovernanceCommandPort();
        var service = CreateService(
            commandPort,
            new FakeServiceLifecycleQueryPort(getResult: null),
            governanceCommandPort,
            new FakeServiceGovernanceQueryPort(),
            new ScopeWorkflowCapabilityOptions(),
            ScopeExplicitRequestAdmissionTestFixture.CreateAdmissionService());
        var request = new ScopeWorkflowUpsertRequest(
            ScopeExplicitRequestAdmissionTestFixture.ScopeId,
            ScopeExplicitRequestAdmissionTestFixture.WorkflowId,
            ScopeExplicitRequestAdmissionTestFixture.WorkflowYaml,
            RevisionId: ScopeExplicitRequestAdmissionTestFixture.RevisionId)
        {
            CapabilityAdmission = ScopeExplicitRequestAdmissionTestFixture.CreateContext(scenario),
        };

        Func<Task> act = async () => await service.UpsertAsync(request);

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be(expectedBlockerCode);
        commandPort.Calls.Should().BeEmpty();
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
            new ScopeWorkflowCapabilityOptions(),
            ScopeExplicitRequestAdmissionTestFixture.CreateAdmissionService());

        await service.UpsertAsync(new ScopeWorkflowUpsertRequest(
            ScopeExplicitRequestAdmissionTestFixture.ScopeId,
            ScopeExplicitRequestAdmissionTestFixture.WorkflowId,
            ScopeExplicitRequestAdmissionTestFixture.WorkflowYaml,
            RevisionId: ScopeExplicitRequestAdmissionTestFixture.RevisionId)
        {
            CapabilityAdmission = ScopeExplicitRequestAdmissionTestFixture.CreateContext("matching"),
        });

        var revision = commandPort.Calls
            .Single(call => call.Method == "CreateRevisionAsync")
            .Command.Should().BeOfType<CreateServiceRevisionCommand>().Subject;
        revision.Spec.Identity.TenantId.Should().Be(ScopeExplicitRequestAdmissionTestFixture.ScopeId);
        revision.Spec.RevisionId.Should().Be(ScopeExplicitRequestAdmissionTestFixture.RevisionId);
        revision.Spec.WorkflowSpec.WorkflowId.Should().Be(ScopeExplicitRequestAdmissionTestFixture.WorkflowId);
        ScopeExplicitRequestAdmissionTestFixture.AssertCallerOwnedGrant(
            revision.Spec.WorkflowSpec.CapabilityAdmissionPlan);
    }

    [Fact]
    public async Task UpsertAsync_WithDifferentBearersAndOpaqueConfirmationBytes_ShouldPersistIdenticalCommandAndArtifact()
    {
        const string firstBearer = "bearer-c1-isolation-alpha";
        const string secondBearer = "bearer-c1-isolation-beta";
        const string firstRawMarker = "raw-confirmation-c1-alpha";
        const string secondRawMarker = "raw-confirmation-c1-beta";
        var firstCommands = new RecordingServiceCommandPort();
        var secondCommands = new RecordingServiceCommandPort();
        var firstContext = ScopeExplicitRequestAdmissionTestFixture.CreateMatchingContext(
            firstBearer,
            firstRawMarker);
        var secondContext = ScopeExplicitRequestAdmissionTestFixture.CreateMatchingContext(
            secondBearer,
            secondRawMarker);
        firstContext.ExplicitRequestConfirmations.Single().ToByteArray().AsSpan()
            .IndexOf(ScopeExplicitRequestAdmissionTestFixture.RawMarkerBytes(firstRawMarker))
            .Should().BeGreaterThanOrEqualTo(0);
        secondContext.ExplicitRequestConfirmations.Single().ToByteArray().AsSpan()
            .IndexOf(ScopeExplicitRequestAdmissionTestFixture.RawMarkerBytes(secondRawMarker))
            .Should().BeGreaterThanOrEqualTo(0);
        var firstService = CreateService(
            firstCommands,
            new FakeServiceLifecycleQueryPort(getResult: null),
            new RecordingServiceGovernanceCommandPort(),
            new FakeServiceGovernanceQueryPort(),
            new ScopeWorkflowCapabilityOptions(),
            ScopeExplicitRequestAdmissionTestFixture.CreateAdmissionService());
        var secondService = CreateService(
            secondCommands,
            new FakeServiceLifecycleQueryPort(getResult: null),
            new RecordingServiceGovernanceCommandPort(),
            new FakeServiceGovernanceQueryPort(),
            new ScopeWorkflowCapabilityOptions(),
            ScopeExplicitRequestAdmissionTestFixture.CreateAdmissionService());

        await firstService.UpsertAsync(new ScopeWorkflowUpsertRequest(
            ScopeExplicitRequestAdmissionTestFixture.ScopeId,
            ScopeExplicitRequestAdmissionTestFixture.WorkflowId,
            ScopeExplicitRequestAdmissionTestFixture.WorkflowYaml,
            RevisionId: ScopeExplicitRequestAdmissionTestFixture.RevisionId)
        {
            CapabilityAdmission = firstContext,
        });
        await secondService.UpsertAsync(new ScopeWorkflowUpsertRequest(
            ScopeExplicitRequestAdmissionTestFixture.ScopeId,
            ScopeExplicitRequestAdmissionTestFixture.WorkflowId,
            ScopeExplicitRequestAdmissionTestFixture.WorkflowYaml,
            RevisionId: ScopeExplicitRequestAdmissionTestFixture.RevisionId)
        {
            CapabilityAdmission = secondContext,
        });

        var firstCommand = firstCommands.Calls
            .Single(call => call.Method == "CreateRevisionAsync")
            .Command.Should().BeOfType<CreateServiceRevisionCommand>().Subject;
        var secondCommand = secondCommands.Calls
            .Single(call => call.Method == "CreateRevisionAsync")
            .Command.Should().BeOfType<CreateServiceRevisionCommand>().Subject;
        var firstCommandBytes = firstCommand.ToByteArray();
        firstCommandBytes.Should().Equal(secondCommand.ToByteArray());
        ScopeExplicitRequestAdmissionTestFixture.GetReachableFieldNames(CreateServiceRevisionCommand.Descriptor)
            .Should().NotContain(fieldName => fieldName.Contains("confirmation", StringComparison.OrdinalIgnoreCase));
        firstCommandBytes.AsSpan().IndexOf(ScopeExplicitRequestAdmissionTestFixture.RawMarkerBytes(firstRawMarker))
            .Should().Be(-1);
        firstCommandBytes.AsSpan().IndexOf(ScopeExplicitRequestAdmissionTestFixture.RawMarkerBytes(secondRawMarker))
            .Should().Be(-1);
        ScopeExplicitRequestAdmissionTestFixture.AssertCallerOwnedGrant(
            firstCommand.Spec.WorkflowSpec.CapabilityAdmissionPlan);

        var firstArtifact = ScopeExplicitRequestAdmissionTestFixture.BuildPreparedArtifact(firstCommand);
        var secondArtifact = ScopeExplicitRequestAdmissionTestFixture.BuildPreparedArtifact(secondCommand);
        ScopeExplicitRequestAdmissionTestFixture.GetReachableFieldNames(PreparedServiceRevisionArtifact.Descriptor)
            .Should().NotContain(fieldName => fieldName.Contains("confirmation", StringComparison.OrdinalIgnoreCase));
        firstArtifact.ArtifactHash.Should().Be(secondArtifact.ArtifactHash);
        firstArtifact.ToByteArray().Should().Equal(secondArtifact.ToByteArray());
        firstArtifact.ToByteArray().AsSpan()
            .IndexOf(ScopeExplicitRequestAdmissionTestFixture.RawMarkerBytes(firstRawMarker))
            .Should().Be(-1);
        firstArtifact.ToByteArray().AsSpan()
            .IndexOf(ScopeExplicitRequestAdmissionTestFixture.RawMarkerBytes(secondRawMarker))
            .Should().Be(-1);
        ScopeExplicitRequestAdmissionTestFixture.AssertCallerOwnedGrant(
            firstArtifact.DeploymentPlan.WorkflowPlan.CapabilityAdmissionPlan);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpsertAsync_WithExistingPlan_ShouldUseCredentialAwareAdmissionPathAndDispatch(
        bool includeCallerCredential)
    {
        var existingPlan = await ScopeExplicitRequestAdmissionTestFixture.CreatePersistedPlanAsync(
            "scope_workflow_upsert");
        var admission = new ScopeExplicitRequestAdmissionTestFixture.DelegatingAdmissionService(
            ScopeExplicitRequestAdmissionTestFixture.CreateAdmissionService());
        var commandPort = new RecordingServiceCommandPort();
        var service = CreateService(
            commandPort,
            new FakeServiceLifecycleQueryPort(getResult: null),
            new RecordingServiceGovernanceCommandPort(),
            new FakeServiceGovernanceQueryPort(),
            new ScopeWorkflowCapabilityOptions(),
            admission);

        var result = await service.UpsertAsync(new ScopeWorkflowUpsertRequest(
            ScopeExplicitRequestAdmissionTestFixture.ScopeId,
            ScopeExplicitRequestAdmissionTestFixture.WorkflowId,
            ScopeExplicitRequestAdmissionTestFixture.WorkflowYaml,
            RevisionId: ScopeExplicitRequestAdmissionTestFixture.RevisionId)
        {
            CapabilityAdmission = ScopeExplicitRequestAdmissionTestFixture.CreatePersistedContext(
                existingPlan,
                includeCallerCredential),
        });

        result.RevisionId.Should().Be(ScopeExplicitRequestAdmissionTestFixture.RevisionId);
        admission.RefreshPersistedCallCount.Should().Be(includeCallerCredential ? 1 : 0);
        admission.RevalidatePersistedCallCount.Should().Be(includeCallerCredential ? 0 : 1);
        admission.AdmitCallCount.Should().Be(0);
        commandPort.Calls.Should().Contain(call => call.Method == "CreateRevisionAsync");
    }

    private static ScopeWorkflowCommandApplicationService CreateService(
        RecordingServiceCommandPort commandPort,
        FakeServiceLifecycleQueryPort lifecyclePort) =>
        CreateService(
            commandPort,
            lifecyclePort,
            new RecordingServiceGovernanceCommandPort(),
            new FakeServiceGovernanceQueryPort());

    private static ScopeWorkflowCommandApplicationService CreateService(
        RecordingServiceCommandPort commandPort,
        FakeServiceLifecycleQueryPort lifecyclePort,
        RecordingServiceGovernanceCommandPort governanceCommandPort,
        FakeServiceGovernanceQueryPort governanceQueryPort) =>
        CreateService(
            commandPort,
            lifecyclePort,
            governanceCommandPort,
            governanceQueryPort,
            new ScopeWorkflowCapabilityOptions());

    private static ScopeWorkflowCommandApplicationService CreateService(
        RecordingServiceCommandPort commandPort,
        FakeServiceLifecycleQueryPort lifecyclePort,
        RecordingServiceGovernanceCommandPort governanceCommandPort,
        FakeServiceGovernanceQueryPort governanceQueryPort,
        ScopeWorkflowCapabilityOptions options,
        IWorkflowExternalCapabilityAdmissionService? capabilityAdmissionService = null) =>
        new(
            commandPort,
            lifecyclePort,
            governanceCommandPort,
            governanceQueryPort,
            Options.Create(options),
            capabilityAdmissionService ?? new PassthroughWorkflowCapabilityAdmissionService(),
            new ScopeExplicitRequestAdmissionTestFixture.RealWorkflowDefinitionParser());

    private static ExternalCapabilitySourceStamp CreateSourceStamp(long sourceVersion) =>
        new()
        {
            SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
            SourceId = "nyxid-keys:caller-alpha",
            SourceVersion = sourceVersion,
            ObservedAt = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 8, 17, 1, 0, 0, TimeSpan.Zero)
                    .AddMinutes(sourceVersion)),
            FreshUntil = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 8, 17, 1, 10, 0, TimeSpan.Zero)
                    .AddMinutes(sourceVersion)),
            ContentDigest = "catalog-digest-alpha",
        };

    private sealed class PassthroughWorkflowCapabilityAdmissionService : IWorkflowExternalCapabilityAdmissionService
    {
        public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
            WorkflowExternalCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(WorkflowCapabilityAdmissionPlanIntegrity.Create(
                request.WorkflowYaml,
                request.InlineWorkflowYamls,
                request.ExecutionMode,
                [],
                []));

        public Task<WorkflowCapabilityAdmissionPlan> RevalidatePersistedAsync(
            PersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(request.Plan.Clone());

        public Task<WorkflowCapabilityAdmissionPlan> RefreshPersistedAsync(
            RefreshPersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(request.Persisted.Plan.Clone());
    }

    private sealed class FixedPlanAdmissionService(WorkflowCapabilityAdmissionPlan plan) :
        IWorkflowExternalCapabilityAdmissionService
    {
        public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
            WorkflowExternalCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(plan.Clone());

        public Task<WorkflowCapabilityAdmissionPlan> RevalidatePersistedAsync(
            PersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(plan.Clone());

        public Task<WorkflowCapabilityAdmissionPlan> RefreshPersistedAsync(
            RefreshPersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(plan.Clone());
    }

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

        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(ActivateServiceRevisionCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("ActivateServiceRevisionAsync", command));
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

        public ServiceRevisionCatalogSnapshot? Revisions { get; init; }

        public FakeServiceLifecycleQueryPort(ServiceCatalogSnapshot? getResult)
        {
            _getResult = getResult;
        }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(_getResult);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(Revisions);

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);
    }

    private sealed class RecordingServiceGovernanceCommandPort : IServiceGovernanceCommandPort
    {
        private static readonly ServiceCommandAcceptedReceipt DefaultReceipt =
            new("governance-actor", "cmd-governance", "corr-governance");

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
}

internal static class ScopeExplicitRequestAdmissionTestFixture
{
    public const string ScopeId = "scope-c1-alpha";
    public const string WorkflowId = "wf-route-c1-alpha";
    public const string ServiceId = "svc-runtime-c1-alpha";
    public const string RevisionId = "rev-c1-alpha";
    public const string CallerId = "caller-c1-alpha";
    public const string BearerToken = "bearer-c1-transient-secret";
    public const string WorkflowYaml = """
        name: wf-definition-c1-alpha
        steps:
          - id: request-c1-alpha
            type: tool_call
            capability:
              nyxid_request:
                user_service_id: usvc-c1-alpha
                method: GET
                path_template: /api/resources/{resource_id}
                body_mode: none
                response_mode: text
            parameters:
              tool: nyxid_proxy
              arguments: '{}'
        """;

    private const string CallSiteId = "wf-definition-c1-alpha/request-c1-alpha";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    public static IWorkflowExternalCapabilityAdmissionService CreateAdmissionService()
    {
        var readiness = new ExternalWorkflowCapabilityReadinessService([new ExplicitRequestSource()]);
        return new WorkflowExternalCapabilityAdmissionService(
            new RealWorkflowDefinitionParser(),
            readiness,
            new FixedTimeProvider());
    }

    public static Task<WorkflowCapabilityAdmissionPlan> CreatePersistedPlanAsync(string sourceKind)
    {
        var context = CreateContext("matching");
        return CreateAdmissionService().AdmitAsync(
            new WorkflowExternalCapabilityAdmissionRequest(
                new ExternalWorkflowCapabilityAccessContext(
                    ScopeId,
                    CallerId,
                    NyxIdCallerCredentialSelection.SourceReadableUserBearer(BearerToken),
                    null),
                WorkflowYaml,
                null,
                sourceKind,
                ExternalCapabilityExecutionMode.Interactive,
                context.ExplicitRequestConfirmations,
                WorkflowId,
                RevisionId));
    }

    public static WorkflowCapabilityAdmissionContext CreatePersistedContext(
        WorkflowCapabilityAdmissionPlan existingPlan,
        bool includeCallerCredential = true) =>
        new(
            CallerId,
            includeCallerCredential
                ? NyxIdCallerCredentialSelection.SourceReadableUserBearer(BearerToken)
                : null,
            executionMode: ExternalCapabilityExecutionMode.Interactive,
            existingPlan: existingPlan);

    public static WorkflowCapabilityAdmissionContext CreateContext(string scenario)
    {
        IReadOnlyList<NyxIdExplicitRequestConfirmation> confirmations = scenario switch
        {
            "missing" => [],
            "matching" => [MatchingConfirmation()],
            "stale_digest" => [StaleDigestConfirmation()],
            "stale_risk" => [StaleRiskConfirmation()],
            "unknown_call_site" => [UnknownCallSiteConfirmation()],
            "duplicate" => [MatchingConfirmation(), MatchingConfirmation()],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        return new WorkflowCapabilityAdmissionContext(
            CallerId,
            NyxIdCallerCredentialSelection.SourceReadableUserBearer(BearerToken),
            executionMode: ExternalCapabilityExecutionMode.Interactive,
            explicitRequestConfirmations: confirmations);
    }

    public static WorkflowCapabilityAdmissionContext CreateMatchingContext(
        string bearerToken,
        string rawConfirmationMarker)
    {
        var confirmation = MatchingConfirmation();
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            output.WriteTag(1000, WireFormat.WireType.LengthDelimited);
            output.WriteString(rawConfirmationMarker);
        }
        confirmation.MergeFrom(stream.ToArray());
        return new WorkflowCapabilityAdmissionContext(
            CallerId,
            NyxIdCallerCredentialSelection.SourceReadableUserBearer(bearerToken),
            executionMode: ExternalCapabilityExecutionMode.Interactive,
            explicitRequestConfirmations: [confirmation]);
    }

    public static byte[] RawMarkerBytes(string marker) => Encoding.UTF8.GetBytes(marker);

    public static IReadOnlyList<string> GetReachableFieldNames(MessageDescriptor root)
    {
        var fieldNames = new List<string>();
        var pending = new Stack<MessageDescriptor>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(root);
        while (pending.TryPop(out var descriptor))
        {
            if (!visited.Add(descriptor.FullName))
                continue;
            foreach (var field in descriptor.Fields.InFieldNumberOrder())
            {
                fieldNames.Add($"{descriptor.FullName}.{field.Name}");
                if (field.FieldType == FieldType.Message)
                    pending.Push(field.MessageType);
            }
        }
        return fieldNames;
    }

    public static PreparedServiceRevisionArtifact BuildPreparedArtifact(
        CreateServiceRevisionCommand command)
    {
        var workflow = new WorkflowParser().Parse(command.Spec.WorkflowSpec.WorkflowYaml);
        var artifact = WorkflowServiceRevisionArtifactBuilder.Build(
            command.Spec,
            workflow.Name,
            WorkflowAuthorizationDependencyEvaluator.Evaluate(workflow),
            command.Spec.WorkflowSpec.CapabilityAdmissionPlan);
        var normalizedArtifact = artifact.Clone();
        normalizedArtifact.ArtifactHash = string.Empty;
        artifact.ArtifactHash = Convert.ToHexString(SHA256.HashData(normalizedArtifact.ToByteArray()));
        return artifact;
    }

    public static void AssertCallerOwnedGrant(WorkflowCapabilityAdmissionPlan? plan)
    {
        plan.Should().NotBeNull();
        var grant = plan!.InvocationAdmissions
            .Should().ContainSingle().Subject.NyxIdExplicitRequestGrant;
        grant.Should().NotBeNull();
        grant.GrantorOwnerSubject.Should().Be(CallerId);
        grant.GrantorOwnerKind.Should().Be(ExternalCapabilityAuthorizationOwnerKind.Personal);
        grant.GrantorAuthority.Should().Be(NyxIdExplicitRequestGrantorAuthority.AevatarWorkflowBinder);
    }

    private static NyxIdExplicitRequestConfirmation MatchingConfirmation() =>
        new()
        {
            CallSiteId = CallSiteId,
            RequestContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdRequestContractDigest(Selector()),
            AttestedRisk = NyxIdOperationRisk.ReadOnly,
            WorkflowId = WorkflowId,
            RevisionId = RevisionId,
        };

    private static NyxIdExplicitRequestConfirmation StaleDigestConfirmation()
    {
        var confirmation = MatchingConfirmation();
        confirmation.RequestContractDigest = "stale-digest-c1";
        return confirmation;
    }

    private static NyxIdExplicitRequestConfirmation StaleRiskConfirmation()
    {
        var confirmation = MatchingConfirmation();
        confirmation.AttestedRisk = NyxIdOperationRisk.Write;
        return confirmation;
    }

    private static NyxIdExplicitRequestConfirmation UnknownCallSiteConfirmation()
    {
        var confirmation = MatchingConfirmation();
        confirmation.CallSiteId = "wf-definition-c1-unknown/request-c1-unknown";
        return confirmation;
    }

    private static NyxIdRequestSelector Selector() =>
        new()
        {
            UserServiceId = "usvc-c1-alpha",
            Method = NyxIdRequestMethod.Get,
            PathTemplate = "/api/resources/{resource_id}",
            BodyMode = NyxIdRequestBodyMode.None,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };

    private sealed class ExplicitRequestSource : IExternalWorkflowCapabilitySource
    {
        public ExternalWorkflowCapabilitySelector.SelectorOneofCase SelectorKind =>
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest;

        public Task<ExternalWorkflowCapabilityDiscoveryResult> ListAsync(
            ExternalWorkflowCapabilityAccessContext access,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalWorkflowCapabilityDiscoveryResult());

        public Task<ExternalCapabilityReadiness> InspectAsync(
            ExternalWorkflowCapabilityAccessContext access,
            ExternalWorkflowCapabilitySelector selector,
            ExternalCapabilityExecutionMode executionMode,
            CancellationToken cancellationToken = default)
        {
            var request = selector.NyxIdRequest.Clone();
            var requestDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdRequestContractDigest(request);
            var readiness = new ExternalCapabilityReadiness
            {
                ExecutionMode = executionMode,
                Status = ExternalCapabilityReadinessStatus.Ready,
                SelectedSelector = selector.Clone(),
                SelectedCapability = new ExternalWorkflowCapabilityRef
                {
                    NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
                    {
                        Request = request,
                        ServiceSlugSnapshot = "service-slug-c1-alpha",
                        ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                            .ComputeNyxIdExplicitRequestProofDigest(
                                requestDigest,
                                "service-slug-c1-alpha"),
                        ExecutionPolicy = new NyxIdOperationExecutionPolicy
                        {
                            Risk = NyxIdOperationRisk.ReadOnly,
                            Approval = NyxIdOperationApproval.None,
                            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                            AllowedExecutionModes = { ExternalCapabilityExecutionMode.Interactive },
                        },
                    },
                },
            };
            readiness.Sources.Add(new ExternalCapabilitySourceStamp
            {
                SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
                SourceId = $"nyxid-keys:caller:{access.CallerId}",
                ObservedAt = Timestamp.FromDateTimeOffset(Now),
                FreshUntil = Timestamp.FromDateTimeOffset(Now.AddMinutes(5)),
                ContentDigest = "source-digest-c1-alpha",
            });
            return Task.FromResult(readiness);
        }
    }

    internal sealed class RealWorkflowDefinitionParser : IWorkflowDefinitionParser
    {
        private readonly WorkflowParser _parser = new();

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var workflow = _parser.Parse(workflowYaml);
                return Task.FromResult(WorkflowYamlParseResult.Success(
                    workflow.Name,
                    WorkflowAuthorizationDependencyEvaluator.Evaluate(workflow)));
            }
            catch (Exception exception)
            {
                return Task.FromResult(WorkflowYamlParseResult.Invalid(exception.Message));
            }
        }

        public async Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default)
        {
            if (inlineWorkflowDocuments.Count == 0)
                return WorkflowInlineYamlBundleParseResult.Invalid("workflowYamls is required.");

            var yamls = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var document in inlineWorkflowDocuments)
            {
                var parse = await ParseWorkflowYamlAsync(document.Yaml, ct);
                if (!parse.Succeeded)
                    return WorkflowInlineYamlBundleParseResult.Invalid(parse.Error);
                yamls.Add(parse.WorkflowName, document.Yaml);
            }

            var entry = inlineWorkflowDocuments[0];
            var entryParse = await ParseWorkflowYamlAsync(entry.Yaml, ct);
            return WorkflowInlineYamlBundleParseResult.Success(
                entryParse.WorkflowName,
                entry.Yaml,
                yamls);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public sealed class DelegatingAdmissionService(
        IWorkflowExternalCapabilityAdmissionService inner) : IWorkflowExternalCapabilityAdmissionService
    {
        public int AdmitCallCount { get; private set; }

        public int RevalidatePersistedCallCount { get; private set; }

        public int RefreshPersistedCallCount { get; private set; }

        public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
            WorkflowExternalCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            AdmitCallCount++;
            return inner.AdmitAsync(request, cancellationToken);
        }

        public Task<WorkflowCapabilityAdmissionPlan> RevalidatePersistedAsync(
            PersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            RevalidatePersistedCallCount++;
            return inner.RevalidatePersistedAsync(request, cancellationToken);
        }

        public Task<WorkflowCapabilityAdmissionPlan> RefreshPersistedAsync(
            RefreshPersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            RefreshPersistedCallCount++;
            return inner.RefreshPersistedAsync(request, cancellationToken);
        }
    }
}
