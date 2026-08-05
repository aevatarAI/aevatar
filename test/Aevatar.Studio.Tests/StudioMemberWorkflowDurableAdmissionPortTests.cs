using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberWorkflowDurableAdmissionPortTests
{
    [Fact]
    public async Task AdmitAsync_ForInteractiveServingRevision_ShouldPreviewAndBindNewImmutableDurableRevision()
    {
        var servingPlan = await CreateAdmissionPlanAsync(
            ExternalCapabilityExecutionMode.Interactive,
            "wf-alpha",
            "rev-interactive-alpha");
        var preview = new RecordingPreviewService();
        var memberService = new RecordingMemberService();
        var revisionCatalog = new RecordingRevisionCatalogReader(
            CreateCatalog(servingPlan, revisionId: "rev-interactive-alpha"));
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService();
        var binding = new RecordingBindingPort();
        var port = new StudioMemberWorkflowDurableAdmissionPort(
            memberService,
            revisionCatalog,
            preview,
            admission,
            binding);
        var request = new StudioMemberWorkflowDurableAdmissionRequest(
            "scope-alpha",
            "m-alpha",
            StudioExplicitRequestAdmissionTestKit.Context(
                executionMode: ExternalCapabilityExecutionMode.Durable));

        var result = await port.AdmitAsync(request);

        result.Status.Should().Be(StudioMemberWorkflowDurableAdmissionStatus.RevisionAccepted);
        result.ReadyForSchedule.Should().BeFalse();
        result.ScopeId.Should().Be("scope-alpha");
        result.TeamId.Should().Be("team-alpha");
        result.MemberId.Should().Be("m-alpha");
        result.WorkflowId.Should().Be("wf-alpha");
        result.PublishedServiceId.Should().Be("svc-alpha");
        result.ServingRevisionId.Should().Be("rev-interactive-alpha");
        result.TargetRevisionId.Should().StartWith("rev-durable-");
        result.TargetRevisionId.Should().NotBe("rev-interactive-alpha");

        revisionCatalog.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-alpha",
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = "svc-alpha",
        });
        preview.LastRequest.Should().NotBeNull();
        preview.LastRequest!.WorkflowYaml.Should().Be(StudioExplicitRequestAdmissionTestKit.WorkflowYaml);
        preview.LastRequest.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Durable);
        preview.LastRequest.WorkflowId.Should().Be("wf-alpha");
        preview.LastRequest.RevisionId.Should().StartWith("rev-durable-provisional-");
        preview.LastRequest.RevisionId.Should().NotBe(result.TargetRevisionId);
        preview.LastRequest.Access.ScopeId.Should().Be("scope-alpha");
        preview.LastRequest.Access.CallerId.Should().Be(StudioExplicitRequestAdmissionTestKit.CallerId);
        preview.LastRequest.Access.NyxIdCallerCredential?.SourceReadableUserBearerToken.Should()
            .Be(StudioExplicitRequestAdmissionTestKit.CallerBearer);
        preview.LastRequest.Access.NyxIdOrganizationBearerToken.Should()
            .Be(StudioExplicitRequestAdmissionTestKit.OrganizationBearer);

        binding.LastRequest.Should().NotBeNull();
        binding.LastRequest!.ScopeId.Should().Be("scope-alpha");
        binding.LastRequest.MemberId.Should().Be("m-alpha");
        binding.LastRequest.WorkflowId.Should().Be("wf-alpha");
        binding.LastRequest.RevisionId.Should().Be(result.TargetRevisionId);
        binding.LastRequest.WorkflowYaml.Should().Be(StudioExplicitRequestAdmissionTestKit.WorkflowYaml);
        binding.LastRequest.CapabilityAdmission!.ExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Durable);
        binding.LastRequest.CapabilityAdmission.ExplicitRequestConfirmations.Should().BeEmpty();
        var durablePlan = binding.LastRequest.CapabilityAdmission.ExistingPlan;
        durablePlan.Should().NotBeNull();
        durablePlan!.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Durable);
        var grant = durablePlan.InvocationAdmissions.Should().ContainSingle().Which
            .NyxIdExplicitRequestGrant;
        grant.Should().NotBeNull();
        grant!.CallSiteId.Should().Be("wf-alpha/request-alpha");
        grant.RequestContractDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdRequestContractDigest(
                StudioExplicitRequestAdmissionTestKit.Selector()));
        grant.Risk.Should().Be(NyxIdOperationRisk.ReadOnly);
        grant.WorkflowId.Should().Be("wf-alpha");
        grant.RevisionId.Should().Be(result.TargetRevisionId);
        durablePlan.DefinitionDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeDefinitionDigest(
                StudioExplicitRequestAdmissionTestKit.WorkflowYaml,
                new Dictionary<string, string>(),
                "wf-alpha",
                result.TargetRevisionId));
        durablePlan.AdmissionDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(durablePlan));
        durablePlan.InvocationAdmissions.Single().Capability.NyxIdUserRequest
            .ExplicitRequestGrantDigest.Should().Be(
                WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdExplicitRequestGrantDigest(grant));
        admission.Requests.Should().ContainSingle();
        admission.Requests.Single().RevisionId.Should().Be(preview.LastRequest.RevisionId);
        memberService.EndpointContractQueryCount.Should().Be(2);
        servingPlan.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
    }

    [Fact]
    public async Task AdmitAsync_WhenNewRevisionIsAlreadyVisible_ShouldReturnReadyWithoutPolling()
    {
        var servingPlan = await CreateAdmissionPlanAsync(
            ExternalCapabilityExecutionMode.Interactive,
            "wf-alpha",
            "rev-interactive-alpha");
        var preview = new RecordingPreviewService();
        var binding = new RecordingBindingPort();
        var memberService = new RecordingMemberService(binding)
        {
            ReturnBoundRevisionAfterFirstRead = true,
        };
        var port = new StudioMemberWorkflowDurableAdmissionPort(
            memberService,
            new RecordingRevisionCatalogReader(
                CreateCatalog(servingPlan, revisionId: "rev-interactive-alpha")),
            preview,
            StudioExplicitRequestAdmissionTestKit.CreateAdmissionService(),
            binding);

        var result = await port.AdmitAsync(new StudioMemberWorkflowDurableAdmissionRequest(
            "scope-alpha",
            "m-alpha",
            StudioExplicitRequestAdmissionTestKit.Context(
                executionMode: ExternalCapabilityExecutionMode.Durable)));

        result.Status.Should().Be(StudioMemberWorkflowDurableAdmissionStatus.RevisionReady);
        result.ReadyForSchedule.Should().BeTrue();
        result.TeamId.Should().Be("team-alpha");
        memberService.EndpointContractQueryCount.Should().Be(2);
    }

    [Fact]
    public async Task AdmitAsync_WhenServingRevisionIsAlreadyDurable_ShouldReturnAuthoritativeTeamIdentity()
    {
        var servingPlan = await CreateAdmissionPlanAsync(
            ExternalCapabilityExecutionMode.Durable,
            "wf-alpha",
            "rev-interactive-alpha");
        var preview = new RecordingPreviewService();
        var binding = new RecordingBindingPort();
        var port = new StudioMemberWorkflowDurableAdmissionPort(
            new RecordingMemberService(),
            new RecordingRevisionCatalogReader(
                CreateCatalog(servingPlan, revisionId: "rev-interactive-alpha")),
            preview,
            StudioExplicitRequestAdmissionTestKit.CreateAdmissionService(),
            binding);

        var result = await port.AdmitAsync(new StudioMemberWorkflowDurableAdmissionRequest(
            "scope-alpha",
            "m-alpha",
            StudioExplicitRequestAdmissionTestKit.Context(
                executionMode: ExternalCapabilityExecutionMode.Durable)));

        result.Status.Should().Be(StudioMemberWorkflowDurableAdmissionStatus.AlreadyDurable);
        result.ReadyForSchedule.Should().BeTrue();
        result.ScopeId.Should().Be("scope-alpha");
        result.TeamId.Should().Be("team-alpha");
        result.MemberId.Should().Be("m-alpha");
        result.WorkflowId.Should().Be("wf-alpha");
        result.PublishedServiceId.Should().Be("svc-alpha");
        result.TargetRevisionId.Should().Be("rev-interactive-alpha");
        preview.CallCount.Should().Be(0);
        binding.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task AdmitAsync_WhenTargetRevisionAlreadyExists_ShouldReuseItWithoutBindingAgain()
    {
        var servingPlan = await CreateAdmissionPlanAsync(
            ExternalCapabilityExecutionMode.Interactive,
            "wf-alpha",
            "rev-interactive-alpha");
        var preview = new RecordingPreviewService();
        var memberService = new RecordingMemberService();
        var revisionCatalog = new RecordingRevisionCatalogReader(
            CreateCatalog(servingPlan, revisionId: "rev-interactive-alpha"));
        var binding = new RecordingBindingPort();
        var port = new StudioMemberWorkflowDurableAdmissionPort(
            memberService,
            revisionCatalog,
            preview,
            StudioExplicitRequestAdmissionTestKit.CreateAdmissionService(),
            binding);
        var request = new StudioMemberWorkflowDurableAdmissionRequest(
            "scope-alpha",
            "m-alpha",
            StudioExplicitRequestAdmissionTestKit.Context(
                executionMode: ExternalCapabilityExecutionMode.Durable));

        var first = await port.AdmitAsync(request);
        var durablePlan = await CreateAdmissionPlanAsync(
            ExternalCapabilityExecutionMode.Durable,
            "wf-alpha",
            first.TargetRevisionId);
        revisionCatalog.Snapshot = CreateCatalog(
            (servingPlan, "rev-interactive-alpha", "artifact-interactive-alpha"),
            (durablePlan, first.TargetRevisionId, "artifact-durable-alpha"));

        var second = await port.AdmitAsync(request);

        second.Status.Should().Be(StudioMemberWorkflowDurableAdmissionStatus.RevisionAccepted);
        second.TargetRevisionId.Should().Be(first.TargetRevisionId);
        binding.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task AdmitAsync_WithDifferentBinder_ShouldDeriveDifferentDurableRevisionIdentity()
    {
        var servingPlan = await CreateAdmissionPlanAsync(
            ExternalCapabilityExecutionMode.Interactive,
            "wf-alpha",
            "rev-interactive-alpha");
        var firstPreview = new RecordingPreviewService();
        var first = await new StudioMemberWorkflowDurableAdmissionPort(
                new RecordingMemberService(),
                new RecordingRevisionCatalogReader(
                    CreateCatalog(servingPlan, revisionId: "rev-interactive-alpha")),
                firstPreview,
                StudioExplicitRequestAdmissionTestKit.CreateAdmissionService(),
                new RecordingBindingPort())
            .AdmitAsync(new StudioMemberWorkflowDurableAdmissionRequest(
                "scope-alpha",
                "m-alpha",
                StudioExplicitRequestAdmissionTestKit.Context(
                    executionMode: ExternalCapabilityExecutionMode.Durable)));
        var secondPreview = new RecordingPreviewService();
        var second = await new StudioMemberWorkflowDurableAdmissionPort(
                new RecordingMemberService(),
                new RecordingRevisionCatalogReader(
                    CreateCatalog(servingPlan, revisionId: "rev-interactive-alpha")),
                secondPreview,
                StudioExplicitRequestAdmissionTestKit.CreateAdmissionService(
                    ownerSubject: "caller-beta"),
                new RecordingBindingPort())
            .AdmitAsync(new StudioMemberWorkflowDurableAdmissionRequest(
                "scope-alpha",
                "m-alpha",
                new WorkflowCapabilityAdmissionContext(
                    "caller-beta",
                    NyxIdCallerCredentialSelection.SourceReadableUserBearer("caller-beta-bearer"),
                    "organization-beta-bearer",
                    ExternalCapabilityExecutionMode.Durable)));

        second.TargetRevisionId.Should().NotBe(first.TargetRevisionId);
    }

    [Fact]
    public async Task AdmitAsync_WithDifferentSourceEvidence_ShouldDeriveDifferentDurableRevisionIdentity()
    {
        var servingPlan = await CreateAdmissionPlanAsync(
            ExternalCapabilityExecutionMode.Interactive,
            "wf-alpha",
            "rev-interactive-alpha");
        var firstPreview = new RecordingPreviewService();
        var first = await new StudioMemberWorkflowDurableAdmissionPort(
                new RecordingMemberService(),
                new RecordingRevisionCatalogReader(
                    CreateCatalog(servingPlan, revisionId: "rev-interactive-alpha")),
                firstPreview,
                StudioExplicitRequestAdmissionTestKit.CreateAdmissionService(sourceVersion: 23),
                new RecordingBindingPort())
            .AdmitAsync(new StudioMemberWorkflowDurableAdmissionRequest(
                "scope-alpha",
                "m-alpha",
                StudioExplicitRequestAdmissionTestKit.Context(
                    executionMode: ExternalCapabilityExecutionMode.Durable)));
        var secondPreview = new RecordingPreviewService();
        var second = await new StudioMemberWorkflowDurableAdmissionPort(
                new RecordingMemberService(),
                new RecordingRevisionCatalogReader(
                    CreateCatalog(servingPlan, revisionId: "rev-interactive-alpha")),
                secondPreview,
                StudioExplicitRequestAdmissionTestKit.CreateAdmissionService(sourceVersion: 24),
                new RecordingBindingPort())
            .AdmitAsync(new StudioMemberWorkflowDurableAdmissionRequest(
                "scope-alpha",
                "m-alpha",
                StudioExplicitRequestAdmissionTestKit.Context(
                    executionMode: ExternalCapabilityExecutionMode.Durable)));

        second.TargetRevisionId.Should().NotBe(first.TargetRevisionId);
    }

    [Fact]
    public async Task AdmitAsync_WhenTopLevelIsDurableButExplicitGrantIsInteractiveOnly_ShouldFailClosed()
    {
        var servingPlan = await CreateAdmissionPlanAsync(
            ExternalCapabilityExecutionMode.Interactive,
            "wf-alpha",
            "rev-interactive-alpha");
        servingPlan.ExecutionMode = ExternalCapabilityExecutionMode.Durable;
        servingPlan.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(servingPlan);
        var preview = new RecordingPreviewService();
        var binding = new RecordingBindingPort();
        var port = new StudioMemberWorkflowDurableAdmissionPort(
            new RecordingMemberService(),
            new RecordingRevisionCatalogReader(
                CreateCatalog(servingPlan, revisionId: "rev-interactive-alpha")),
            preview,
            StudioExplicitRequestAdmissionTestKit.CreateAdmissionService(),
            binding);

        var action = () => port.AdmitAsync(new StudioMemberWorkflowDurableAdmissionRequest(
            "scope-alpha",
            "m-alpha",
            StudioExplicitRequestAdmissionTestKit.Context(
                executionMode: ExternalCapabilityExecutionMode.Durable)));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("serving_revision_admission_plan_invalid");
        preview.CallCount.Should().Be(0);
        binding.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task AdmitAsync_WhenArtifactIdentityDoesNotMatchServingTarget_ShouldFailBeforePreviewOrBinding()
    {
        var servingPlan = await CreateAdmissionPlanAsync(
            ExternalCapabilityExecutionMode.Interactive,
            "wf-alpha",
            "rev-interactive-alpha");
        var catalog = CreateCatalog(servingPlan, revisionId: "rev-interactive-alpha");
        catalog.Revisions.Single().PreparedArtifact!.Identity.ServiceId = "svc-other";
        var preview = new RecordingPreviewService();
        var binding = new RecordingBindingPort();
        var port = new StudioMemberWorkflowDurableAdmissionPort(
            new RecordingMemberService(),
            new RecordingRevisionCatalogReader(catalog),
            preview,
            StudioExplicitRequestAdmissionTestKit.CreateAdmissionService(),
            binding);

        var action = () => port.AdmitAsync(new StudioMemberWorkflowDurableAdmissionRequest(
            "scope-alpha",
            "m-alpha",
            StudioExplicitRequestAdmissionTestKit.Context(
                executionMode: ExternalCapabilityExecutionMode.Durable)));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("serving_revision_artifact_identity_mismatch");
        preview.CallCount.Should().Be(0);
        binding.CallCount.Should().Be(0);
    }

    private static async Task<WorkflowCapabilityAdmissionPlan> CreateAdmissionPlanAsync(
        ExternalCapabilityExecutionMode executionMode,
        string workflowId,
        string revisionId)
    {
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService();
        return await admission.AdmitAsync(new WorkflowExternalCapabilityAdmissionRequest(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-alpha",
                StudioExplicitRequestAdmissionTestKit.CallerId,
                NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                    StudioExplicitRequestAdmissionTestKit.CallerBearer),
                StudioExplicitRequestAdmissionTestKit.OrganizationBearer),
            StudioExplicitRequestAdmissionTestKit.WorkflowYaml,
            new Dictionary<string, string>(),
            "test_serving_revision",
            executionMode,
            [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(workflowId, revisionId)],
            workflowId,
            revisionId));
    }

    private static ServiceRevisionCatalogSnapshot CreateCatalog(
        WorkflowCapabilityAdmissionPlan plan,
        string revisionId) =>
        CreateCatalog((plan, revisionId, "artifact-interactive-alpha"));

    private static ServiceRevisionCatalogSnapshot CreateCatalog(
        params (WorkflowCapabilityAdmissionPlan Plan, string RevisionId, string ArtifactHash)[] revisions)
    {
        return new ServiceRevisionCatalogSnapshot(
            "scope-alpha/default/services/svc-alpha",
            revisions.Select(static revision => CreateRevisionSnapshot(
                    revision.Plan,
                    revision.RevisionId,
                    revision.ArtifactHash))
                .ToArray(),
            DateTimeOffset.Parse("2026-08-01T00:00:02Z"),
            StateVersion: 41);
    }

    private static ServiceRevisionSnapshot CreateRevisionSnapshot(
        WorkflowCapabilityAdmissionPlan plan,
        string revisionId,
        string artifactHash)
    {
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = new ServiceIdentity
            {
                TenantId = "scope-alpha",
                AppId = ScopeServiceIdentityDefaults.ServiceAppId,
                Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
                ServiceId = "svc-alpha",
            },
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            ArtifactHash = artifactHash,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    WorkflowName = "wf-alpha",
                    WorkflowYaml = StudioExplicitRequestAdmissionTestKit.WorkflowYaml,
                    WorkflowId = "wf-alpha",
                    RevisionId = revisionId,
                    CapabilityAdmissionPlan = plan.Clone(),
                },
            },
        };
        return new ServiceRevisionSnapshot(
            revisionId,
            ServiceImplementationKind.Workflow.ToString(),
            ServiceRevisionStatus.Published.ToString(),
            artifact.ArtifactHash,
            string.Empty,
            [],
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-01T00:00:01Z"),
            DateTimeOffset.Parse("2026-08-01T00:00:02Z"),
            null,
            PreparedArtifact: artifact);
    }

    private static StudioMemberDetailResponse CreateMemberDetail() =>
        new(
            new StudioMemberSummaryResponse(
                MemberId: "m-alpha",
                ScopeId: "scope-alpha",
                DisplayName: "Alpha",
                Description: string.Empty,
                ImplementationKind: MemberImplementationKindNames.Workflow,
                LifecycleStage: "active",
                PublishedServiceId: "svc-alpha",
                LastBoundRevisionId: "rev-interactive-alpha",
                CreatedAt: DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
                UpdatedAt: DateTimeOffset.Parse("2026-08-01T00:00:00Z"))
            {
                TeamId = "team-alpha",
            },
            new StudioMemberImplementationRefResponse(
                MemberImplementationKindNames.Workflow,
                WorkflowId: "wf-alpha",
                WorkflowRevision: "rev-interactive-alpha"),
            new StudioMemberBindingContractResponse(
                "svc-alpha",
                "rev-interactive-alpha",
                MemberImplementationKindNames.Workflow,
                DateTimeOffset.Parse("2026-08-01T00:00:00Z")));

    private static StudioMemberEndpointContractResponse CreateEndpointContract(string revisionId) =>
        new(
            ScopeId: "scope-alpha",
            MemberId: "m-alpha",
            PublishedServiceId: "svc-alpha",
            EndpointId: "chat",
            InvokePath: "/api/scopes/scope-alpha/members/m-alpha/invoke/chat",
            Method: "POST",
            RequestContentType: "application/json",
            ResponseContentType: "text/event-stream",
            RequestTypeUrl: "aevatar.workflow.ChatRequestEvent",
            ResponseTypeUrl: string.Empty,
            SupportsSse: true,
            SupportsWebSocket: false,
            SupportsAguiFrames: true,
            StreamFrameFormat: "agui",
            SmokeTestSupported: true,
            DefaultSmokeInputMode: "prompt",
            DefaultSmokePrompt: "test",
            SampleRequestJson: null,
            DeploymentStatus: "active",
            RevisionId: revisionId,
            InvocationReadiness: new StudioMemberInvocationReadinessResponse(
                true,
                StudioMemberInvocationReadinessStatusNames.Ready,
                string.Empty,
                "Ready.",
                revisionId));

    private sealed class RecordingPreviewService : IWorkflowExplicitRequestPreviewService
    {
        public int CallCount { get; private set; }
        public WorkflowExplicitRequestPreviewRequest? LastRequest { get; private set; }

        public Task<WorkflowExplicitRequestPreviewResult> PreviewAsync(
            WorkflowExplicitRequestPreviewRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(new WorkflowExplicitRequestPreviewResult(
                request.WorkflowId!,
                request.RevisionId!,
                [
                    new WorkflowExplicitRequestPreviewItem(
                        "wf-alpha/request-alpha",
                        WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdRequestContractDigest(
                            StudioExplicitRequestAdmissionTestKit.Selector()),
                        "usvc-alpha",
                        NyxIdRequestMethod.Get,
                        "/api/resources/{resource_id}",
                        NyxIdRequestBodyMode.None,
                        false,
                        NyxIdRequestResponseMode.Text,
                        NyxIdOperationRisk.ReadOnly,
                        false,
                        [
                            ExternalCapabilityExecutionMode.Interactive,
                            ExternalCapabilityExecutionMode.Durable,
                        ]),
                ]));
        }
    }

    private sealed class RecordingBindingPort : IStudioMemberWorkflowBindingPort
    {
        public int CallCount { get; private set; }
        public StudioMemberWorkflowBindingRequest? LastRequest { get; private set; }

        public Task<StudioMemberWorkflowBindingResult> BindAsync(
            StudioMemberWorkflowBindingRequest request,
            CancellationToken ct = default)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(new StudioMemberWorkflowBindingResult(
                true,
                request.ScopeId,
                request.MemberId,
                StudioMemberWorkflowBindingOperationNames.SaveAndBind,
                "accepted",
                WorkflowId: request.WorkflowId,
                RevisionId: request.RevisionId));
        }
    }

    private sealed class RecordingRevisionCatalogReader(ServiceRevisionCatalogSnapshot snapshot)
        : IServiceRevisionCatalogQueryReader
    {
        public ServiceIdentity? Identity { get; private set; }

        public ServiceRevisionCatalogSnapshot Snapshot { get; set; } = snapshot;

        public Task<ServiceRevisionCatalogSnapshot?> GetAsync(
            ServiceIdentity identity,
            CancellationToken ct = default)
        {
            Identity = identity.Clone();
            return Task.FromResult<ServiceRevisionCatalogSnapshot?>(Snapshot);
        }
    }

    private sealed class RecordingMemberService(RecordingBindingPort? binding = null) : IStudioMemberService
    {
        public bool ReturnBoundRevisionAfterFirstRead { get; init; }
        public int EndpointContractQueryCount { get; private set; }

        public Task<StudioMemberDetailResponse> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            Task.FromResult(CreateMemberDetail());

        public Task<StudioMemberEndpointContractResponse?> GetEndpointContractAsync(
            string scopeId,
            string memberId,
            string endpointId,
            CancellationToken ct = default)
        {
            EndpointContractQueryCount++;
            var revisionId = ReturnBoundRevisionAfterFirstRead && EndpointContractQueryCount > 1
                ? binding?.LastRequest?.RevisionId ?? "rev-interactive-alpha"
                : "rev-interactive-alpha";
            return Task.FromResult<StudioMemberEndpointContractResponse?>(
                CreateEndpointContract(revisionId));
        }

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId,
            CreateStudioMemberRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingAcceptedResponse> BindAsync(
            string scopeId,
            string memberId,
            UpdateStudioMemberBindingRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingViewResponse> GetBindingAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingRunStatusResponse> GetBindingRunAsync(
            string scopeId,
            string memberId,
            string bindingRunId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingActivationResponse> ActivateBindingRevisionAsync(
            string scopeId,
            string memberId,
            string revisionId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingRevisionActionResponse> RetireBindingRevisionAsync(
            string scopeId,
            string memberId,
            string revisionId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberCommandResponse> UpdateAsync(
            string scopeId,
            string memberId,
            UpdateStudioMemberRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberCommandResponse> DeleteAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
