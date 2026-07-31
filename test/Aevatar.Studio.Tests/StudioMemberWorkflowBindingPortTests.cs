using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberWorkflowBindingPortTests
{
    [Fact]
    public async Task BindAsync_WithMatchingExplicitConfirmation_ShouldAdmitUnpublishedMember()
    {
        var memberService = new RecordingMemberService { ThrowMemberNotFoundOnGet = true };
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService();
        var saveAndBindPort = new RecordingSaveAndBindPort();
        var memberCommandPort = new RecordingMemberCommandPort();
        var port = new StudioMemberWorkflowBindingPort(
            memberService,
            admission,
            saveAndBindPort,
            memberCommandPort);

        await port.BindAsync(new StudioMemberWorkflowBindingRequest(
            "scope-studio-alpha",
            "m-alpha",
            StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
        {
            WorkflowId = "wf-alpha",
            RevisionId = "rev-alpha",
            CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation("wf-alpha", "rev-alpha")]),
        });

        var admissionRequest = admission.Requests.Should().ContainSingle().Which;
        admissionRequest.Access.NyxIdCallerCredential?.SourceReadableUserBearerToken.Should()
            .Be(StudioExplicitRequestAdmissionTestKit.CallerBearer);
        admissionRequest.Access.NyxIdOrganizationBearerToken.Should()
            .Be(StudioExplicitRequestAdmissionTestKit.OrganizationBearer);
        admissionRequest.ExplicitRequestConfirmations.Should().ContainSingle();
        memberService.LastMemberId.Should().Be("m-alpha");
        memberService.LastRequest!.Workflow!.WorkflowId.Should().Be("wf-alpha");
        memberService.LastRequest.RevisionId.Should().Be("rev-alpha");
        memberService.LastRequest.Workflow.CapabilityAdmissionPlan!.InvocationAdmissions
            .Should().ContainSingle().Which.NyxIdExplicitRequestGrant.GrantorOwnerSubject.Should()
            .Be(StudioExplicitRequestAdmissionTestKit.CallerId);
        memberService.LastRequest.CapabilityAdmission!.NyxIdCallerCredential.Should().BeNull();
        memberService.LastRequest.CapabilityAdmission.NyxIdOrganizationBearerToken.Should().BeNull();
        memberService.LastRequest.CapabilityAdmission.ExplicitRequestConfirmations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing", "NYXID_EXPLICIT_REQUEST_GRANT_REQUIRED")]
    [InlineData("unknown", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH")]
    [InlineData("duplicate", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH")]
    [InlineData("stale_digest", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_DIGEST_MISMATCH")]
    [InlineData("stale_risk", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_RISK_MISMATCH")]
    public async Task BindAsync_WithInvalidExplicitConfirmation_ShouldNotReadOrMutateStudioOrScope(
        string scenario,
        string expectedCode)
    {
        var memberService = new RecordingMemberService();
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService();
        var saveAndBindPort = new RecordingSaveAndBindPort();
        var memberCommandPort = new RecordingMemberCommandPort();
        var port = new StudioMemberWorkflowBindingPort(
            memberService,
            admission,
            saveAndBindPort,
            memberCommandPort);

        var action = () => port.BindAsync(new StudioMemberWorkflowBindingRequest(
            "scope-studio-alpha",
            "m-alpha",
            StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
        {
            WorkflowId = "wf-alpha",
            RevisionId = "rev-alpha",
            CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                StudioExplicitRequestAdmissionTestKit.Confirmations(
                    scenario,
                    "wf-alpha",
                    "rev-alpha")),
        });

        var exception = await action.Should()
            .ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle()
            .Which.Code.Should().Be(expectedCode);
        memberService.GetCallCount.Should().Be(0);
        memberService.LastRequest.Should().BeNull();
        saveAndBindPort.LastRequest.Should().BeNull();
        memberCommandPort.LastRecordPublishedBinding.Should().BeNull();
    }

    [Fact]
    public async Task BindAsync_WithMatchingExplicitConfirmation_ShouldSaveAndBindPublishedMemberWithoutCredentials()
    {
        var memberService = new RecordingMemberService
        {
            Detail = BuildMemberDetail(
                publishedServiceId: "svc-published-alpha",
                lastBoundRevisionId: "rev-existing-alpha",
                memberId: "m-alpha",
                scopeId: "scope-studio-alpha"),
        };
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService();
        var saveAndBindPort = new RecordingSaveAndBindPort();
        var memberCommandPort = new RecordingMemberCommandPort();
        var port = new StudioMemberWorkflowBindingPort(
            memberService,
            admission,
            saveAndBindPort,
            memberCommandPort);

        await port.BindAsync(new StudioMemberWorkflowBindingRequest(
            "scope-studio-alpha",
            "m-alpha",
            StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
        {
            WorkflowId = "wf-alpha",
            RevisionId = "rev-route-alpha",
            CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(
                    "wf-alpha",
                    "rev-route-alpha")]),
        });

        var admissionRequest = admission.Requests.Should().ContainSingle().Which;
        admissionRequest.Access.NyxIdCallerCredential?.SourceReadableUserBearerToken.Should()
            .Be(StudioExplicitRequestAdmissionTestKit.CallerBearer);
        admissionRequest.Access.NyxIdOrganizationBearerToken.Should()
            .Be(StudioExplicitRequestAdmissionTestKit.OrganizationBearer);
        saveAndBindPort.LastRequest.Should().NotBeNull();
        var saved = saveAndBindPort.LastRequest!;
        saved.ScopeId.Should().Be("scope-studio-alpha");
        saved.WorkflowId.Should().Be("wf-alpha");
        saved.ServiceId.Should().Be("svc-published-alpha");
        saved.CapabilityAdmission!.NyxIdCallerCredential.Should().BeNull();
        saved.CapabilityAdmission.NyxIdOrganizationBearerToken.Should().BeNull();
        saved.CapabilityAdmission.ExplicitRequestConfirmations.Should().BeEmpty();
        saved.CapabilityAdmission.ExistingPlan!.InvocationAdmissions.Should().ContainSingle()
            .Which.NyxIdExplicitRequestGrant.GrantorOwnerSubject.Should()
            .Be(StudioExplicitRequestAdmissionTestKit.CallerId);
        saved.CapabilityAdmission.ExistingPlan.ToString().Should()
            .NotContain(StudioExplicitRequestAdmissionTestKit.CallerBearer);
        saved.CapabilityAdmission.ExistingPlan.ToString().Should()
            .NotContain(StudioExplicitRequestAdmissionTestKit.OrganizationBearer);
        memberCommandPort.LastMemberId.Should().Be("m-alpha");
        memberCommandPort.LastRecordPublishedBinding!.PublishedServiceId.Should()
            .Be("svc-published-alpha");
        memberCommandPort.LastRecordPublishedBinding.RevisionId.Should().Be("revision-new");
    }

    [Fact]
    public async Task BindAsync_WithExistingPlan_ShouldOnlyRevalidateWithoutFreshConfirmation()
    {
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService();
        var plan = await admission.AdmitAsync(new WorkflowExternalCapabilityAdmissionRequest(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-studio-alpha",
                StudioExplicitRequestAdmissionTestKit.CallerId,
                NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                    StudioExplicitRequestAdmissionTestKit.CallerBearer),
                StudioExplicitRequestAdmissionTestKit.OrganizationBearer),
            StudioExplicitRequestAdmissionTestKit.WorkflowYaml,
            new Dictionary<string, string>(),
            "test_prepare_plan",
            ExternalCapabilityExecutionMode.Interactive,
            [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation("wf-alpha", "rev-alpha")],
            workflowId: "wf-alpha",
            revisionId: "rev-alpha"));
        admission.Requests.Clear();
        var memberService = new RecordingMemberService { ThrowMemberNotFoundOnGet = true };
        var port = new StudioMemberWorkflowBindingPort(
            memberService,
            admission,
            new RecordingSaveAndBindPort(),
            new RecordingMemberCommandPort());

        await port.BindAsync(new StudioMemberWorkflowBindingRequest(
            "scope-studio-alpha",
            "m-alpha",
            StudioExplicitRequestAdmissionTestKit.WorkflowYaml)
        {
            WorkflowId = "wf-alpha",
            RevisionId = "rev-alpha",
            CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(existingPlan: plan),
        });

        admission.Requests.Should().BeEmpty();
        admission.PersistedRequests.Should().ContainSingle();
        memberService.LastRequest.Should().NotBeNull();
    }

    [Fact]
    public async Task BindAsync_WhenWorkflowIdMissing_ShouldDeriveStableWorkflowId()
    {
        var memberService = new RecordingMemberService();
        var saveAndBindPort = new RecordingSaveAndBindPort();
        var memberCommandPort = new RecordingMemberCommandPort();
        var port = new StudioMemberWorkflowBindingPort(memberService, new StudioWorkflowCapabilityAdmissionTestService(), saveAndBindPort, memberCommandPort);

        await port.BindAsync(new StudioMemberWorkflowBindingRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            WorkflowYaml: "name: demo\nsteps: []\n"));

        memberService.LastScopeId.Should().Be("scope-1");
        memberService.LastMemberId.Should().Be("member-1");
        memberService.LastRequest.Should().NotBeNull();
        memberService.LastRequest!.Workflow.Should().NotBeNull();
        memberService.LastRequest.Workflow!.WorkflowId.Should().StartWith("workflow-");
        memberService.LastRequest.Workflow.WorkflowId.Should().NotBe("workflow-member-1");
        memberService.LastRequest.Workflow.WorkflowId.Should().HaveLength("workflow-".Length + 32);
        memberService.LastRequest.Workflow.WorkflowYamls.Should().ContainSingle()
            .Which.Should().Contain("name: demo");
        memberService.LastRequest.Workflow.CapabilityAdmissionPlan.Should().NotBeNull();
    }

    [Fact]
    public async Task BindAsync_WhenCapabilityAdmissionFails_ShouldNotReadOrMutateMember()
    {
        var memberService = new RecordingMemberService();
        var admission = new StudioWorkflowCapabilityAdmissionTestService(
            new InvalidOperationException("external capability is not ready"));
        var saveAndBindPort = new RecordingSaveAndBindPort();
        var memberCommandPort = new RecordingMemberCommandPort();
        var port = new StudioMemberWorkflowBindingPort(
            memberService,
            admission,
            saveAndBindPort,
            memberCommandPort);

        var action = () => port.BindAsync(new StudioMemberWorkflowBindingRequest(
            "scope-1",
            "member-1",
            "name: demo\nsteps: []\n")
        {
            CapabilityAdmission = new WorkflowCapabilityAdmissionContext(
                "caller-alpha",
                NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                    "runtime-caller-credential")),
        });

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("external capability is not ready");
        var request = admission.Requests.Should().ContainSingle().Which;
        request.Access.ScopeId.Should().Be("scope-1");
        request.Access.CallerId.Should().Be("caller-alpha");
        request.Access.NyxIdCallerCredential?.SourceReadableUserBearerToken
            .Should().Be("runtime-caller-credential");
        request.SourceKind.Should().Be("studio_member_workflow_binding");
        request.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        memberService.GetCallCount.Should().Be(0);
        memberService.LastRequest.Should().BeNull();
        saveAndBindPort.LastRequest.Should().BeNull();
        memberCommandPort.LastRecordPublishedBinding.Should().BeNull();
    }

    [Fact]
    public async Task BindAsync_ShouldPreserveTypedRebindReadiness_ForPersistedV2Plan()
    {
        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.AdmissionRebindRequired,
            Blockers =
            {
                new ExternalCapabilityBlocker
                {
                    Status = ExternalCapabilityReadinessStatus.AdmissionRebindRequired,
                    Code = WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode,
                    SafeMessage = "The persisted workflow capability admission plan must be rebound.",
                },
            },
        };
        var admission = new StudioWorkflowCapabilityAdmissionTestService(
            new WorkflowExternalCapabilityAdmissionException(readiness));
        var memberService = new RecordingMemberService();
        var saveAndBindPort = new RecordingSaveAndBindPort();
        var memberCommandPort = new RecordingMemberCommandPort();
        var port = new StudioMemberWorkflowBindingPort(
            memberService,
            admission,
            saveAndBindPort,
            memberCommandPort);
        var legacyPlan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.LegacySchemaVersion,
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
        };

        var action = () => port.BindAsync(new StudioMemberWorkflowBindingRequest(
            "scope-1",
            "member-1",
            "name: legacy\nsteps: []\n")
        {
            CapabilityAdmission = new WorkflowCapabilityAdmissionContext(
                "caller-alpha",
                existingPlan: legacyPlan),
        });

        var exception = await action.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be(WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode);
        admission.PersistedRequests.Should().ContainSingle().Which.SourceKind.Should()
            .Be("studio_member_workflow_binding");
        memberService.GetCallCount.Should().Be(0);
        memberService.LastRequest.Should().BeNull();
        saveAndBindPort.LastRequest.Should().BeNull();
        memberCommandPort.LastRecordPublishedBinding.Should().BeNull();
    }

    [Fact]
    public async Task BindAsync_WhenWorkflowIdMissing_ShouldConvergePerScopeAndMember()
    {
        var first = await BindWithoutWorkflowIdAsync("scope-1", "member-1");
        var second = await BindWithoutWorkflowIdAsync("scope-1", "member-1");
        var differentScope = await BindWithoutWorkflowIdAsync("scope-2", "member-1");

        first.Should().Be(second);
        differentScope.Should().NotBe(first);
    }

    [Fact]
    public async Task BindAsync_WhenWorkflowIdProvided_ShouldUseTrimmedWorkflowId()
    {
        var memberService = new RecordingMemberService();
        var saveAndBindPort = new RecordingSaveAndBindPort();
        var memberCommandPort = new RecordingMemberCommandPort();
        var port = new StudioMemberWorkflowBindingPort(memberService, new StudioWorkflowCapabilityAdmissionTestService(), saveAndBindPort, memberCommandPort);

        await port.BindAsync(new StudioMemberWorkflowBindingRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            WorkflowYaml: "name: demo\nsteps: []\n")
        {
            WorkflowId = " workflow-explicit ",
        });

        memberService.LastRequest.Should().NotBeNull();
        memberService.LastRequest!.Workflow.Should().NotBeNull();
        memberService.LastRequest.Workflow!.WorkflowId.Should().Be("workflow-explicit");
    }

    [Fact]
    public async Task BindAsync_ShouldReturnAcceptedReceiptWithoutPollingBindingRun()
    {
        var memberService = new RecordingMemberService();
        memberService.EnqueueBindingRun(StudioMemberBindingRunStatusNames.PlatformBindingPending);
        memberService.EnqueueBindingRun(StudioMemberBindingRunStatusNames.Succeeded);
        var saveAndBindPort = new RecordingSaveAndBindPort();
        var memberCommandPort = new RecordingMemberCommandPort();
        var port = new StudioMemberWorkflowBindingPort(memberService, new StudioWorkflowCapabilityAdmissionTestService(), saveAndBindPort, memberCommandPort);

        var result = await port.BindAsync(new StudioMemberWorkflowBindingRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            WorkflowYaml: "name: demo\nsteps: []\n"));

        result.Success.Should().BeTrue();
        result.Operation.Should().Be(StudioMemberWorkflowBindingOperationNames.Bind);
        result.Status.Should().Be(StudioMemberBindingRunStatusNames.Accepted);
        result.BindingRunId.Should().Be("bind-run-1");
        result.AckStage.Should().Be(StudioMemberBindingAckStageNames.DispatchAccepted);
        result.BindingRunRole.Should().Be(StudioMemberBindingRunRoleNames.Candidate);
        result.RevisionId.Should().BeNull();
        memberService.GetBindingRunCallCount.Should().Be(0);
        saveAndBindPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task BindAsync_WhenBindingRunReadModelIsNotMaterializedYet_ShouldNotQueryIt()
    {
        var memberService = new RecordingMemberService
        {
            MissingBindingRunReadCount = 1,
        };
        memberService.EnqueueBindingRun(StudioMemberBindingRunStatusNames.Succeeded);
        var saveAndBindPort = new RecordingSaveAndBindPort();
        var memberCommandPort = new RecordingMemberCommandPort();
        var port = new StudioMemberWorkflowBindingPort(memberService, new StudioWorkflowCapabilityAdmissionTestService(), saveAndBindPort, memberCommandPort);

        var result = await port.BindAsync(new StudioMemberWorkflowBindingRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            WorkflowYaml: "name: demo\nsteps: []\n"));

        result.Status.Should().Be(StudioMemberBindingRunStatusNames.Accepted);
        memberService.GetBindingRunCallCount.Should().Be(0);
    }

    [Fact]
    public async Task BindAsync_WhenBindingRunLaterFails_ShouldStillReturnAcceptedReceipt()
    {
        var memberService = new RecordingMemberService();
        memberService.EnqueueBindingRun(
            StudioMemberBindingRunStatusNames.Failed,
            new StudioMemberBindingFailureResponse(
                Code: "STUDIO_MEMBER_PLATFORM_BINDING_FAILED",
                Message: "workflow parse failed",
                FailedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z")));
        var saveAndBindPort = new RecordingSaveAndBindPort();
        var memberCommandPort = new RecordingMemberCommandPort();
        var port = new StudioMemberWorkflowBindingPort(memberService, new StudioWorkflowCapabilityAdmissionTestService(), saveAndBindPort, memberCommandPort);

        var result = await port.BindAsync(new StudioMemberWorkflowBindingRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            WorkflowYaml: "name: demo\nsteps: []\n"));

        result.Status.Should().Be(StudioMemberBindingRunStatusNames.Accepted);
        result.BindingRunId.Should().Be("bind-run-1");
        memberService.GetBindingRunCallCount.Should().Be(0,
            "dispatch acceptance must not be reclassified from an eventually consistent read model");
    }

    [Fact]
    public async Task BindAsync_WhenMemberReadModelNotMaterialized_ShouldStillDispatchBindingRun()
    {
        var memberService = new RecordingMemberService
        {
            ThrowMemberNotFoundOnGet = true,
        };
        var saveAndBindPort = new RecordingSaveAndBindPort();
        var memberCommandPort = new RecordingMemberCommandPort();
        var port = new StudioMemberWorkflowBindingPort(memberService, new StudioWorkflowCapabilityAdmissionTestService(), saveAndBindPort, memberCommandPort);

        var result = await port.BindAsync(new StudioMemberWorkflowBindingRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            WorkflowYaml: "name: demo\nsteps: []\n"));

        result.Operation.Should().Be(StudioMemberWorkflowBindingOperationNames.Bind);
        result.Status.Should().Be(StudioMemberBindingRunStatusNames.Accepted);
        memberService.LastRequest.Should().NotBeNull();
        memberService.GetBindingRunCallCount.Should().Be(0);
        saveAndBindPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task BindAsync_WhenMemberAlreadyPublished_ShouldSaveAndBindPublishedService()
    {
        var memberService = new RecordingMemberService
        {
            Detail = BuildMemberDetail(
                publishedServiceId: "published-service-1",
                lastBoundRevisionId: "revision-existing",
                lastBinding: new StudioMemberBindingContractResponse(
                    PublishedServiceId: "published-service-1",
                    RevisionId: "revision-existing",
                    ImplementationKind: "workflow",
                    BoundAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z"))),
        };
        var saveAndBindPort = new RecordingSaveAndBindPort();
        var memberCommandPort = new RecordingMemberCommandPort();
        var port = new StudioMemberWorkflowBindingPort(memberService, new StudioWorkflowCapabilityAdmissionTestService(), saveAndBindPort, memberCommandPort);

        var result = await port.BindAsync(new StudioMemberWorkflowBindingRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            WorkflowYaml: "name: demo\nsteps: []\n")
        {
            WorkflowId = " workflow-explicit ",
        });

        result.Success.Should().BeTrue();
        result.Operation.Should().Be(StudioMemberWorkflowBindingOperationNames.SaveAndBind);
        result.Status.Should().Be("accepted");
        result.WorkflowId.Should().Be("workflow-explicit");
        result.RevisionId.Should().Be("revision-new");
        result.BindingRunId.Should().BeNull();
        memberService.LastRequest.Should().BeNull();
        memberService.GetBindingRunCallCount.Should().Be(0);
        saveAndBindPort.LastRequest.Should().NotBeNull();
        saveAndBindPort.LastRequest!.ScopeId.Should().Be("scope-1");
        saveAndBindPort.LastRequest.ServiceId.Should().Be("published-service-1");
        saveAndBindPort.LastRequest.AppId.Should().Be("studio");
        saveAndBindPort.LastRequest.ExposureDesired.Should().BeTrue();
        saveAndBindPort.LastRequest.DisplayName.Should().Be("Member One");
        saveAndBindPort.LastRequest.WorkflowId.Should().Be("workflow-explicit");
        saveAndBindPort.LastRequest.CapabilityAdmission.Should().NotBeNull();
        saveAndBindPort.LastRequest.CapabilityAdmission!.ExistingPlan.Should().NotBeNull();
        memberCommandPort.LastRecordPublishedBinding.Should().NotBeNull();
        memberCommandPort.LastScopeId.Should().Be("scope-1");
        memberCommandPort.LastMemberId.Should().Be("member-1");
        memberCommandPort.LastRecordPublishedBinding!.PublishedServiceId.Should().Be("published-service-1");
        memberCommandPort.LastRecordPublishedBinding.RevisionId.Should().Be("revision-new");
        memberCommandPort.LastRecordPublishedBinding.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
        memberCommandPort.LastRecordPublishedBinding.ImplementationRef.WorkflowId.Should().Be("workflow-explicit");
        memberCommandPort.LastRecordPublishedBinding.ImplementationRef.WorkflowRevision.Should().Be("revision-new");
        memberCommandPort.LastRecordPublishedBinding.ExpectedActorId.Should().Be("workflow-definition:workflow-new");
    }

    [Fact]
    public async Task BindAsync_WhenPublishedMemberKindIsNotWorkflow_ShouldRejectBeforeSaveAndBind()
    {
        var memberService = new RecordingMemberService
        {
            Detail = BuildMemberDetail(
                publishedServiceId: "published-service-1",
                lastBoundRevisionId: "revision-existing",
                implementationKind: MemberImplementationKindNames.Script),
        };
        var saveAndBindPort = new RecordingSaveAndBindPort();
        var memberCommandPort = new RecordingMemberCommandPort();
        var port = new StudioMemberWorkflowBindingPort(memberService, new StudioWorkflowCapabilityAdmissionTestService(), saveAndBindPort, memberCommandPort);

        var action = () => port.BindAsync(new StudioMemberWorkflowBindingRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            WorkflowYaml: "name: demo\nsteps: []\n"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Studio member 'member-1' implementation kind 'script' cannot be bound with a workflow.");
        saveAndBindPort.LastRequest.Should().BeNull();
        memberCommandPort.LastRecordPublishedBinding.Should().BeNull();
    }

    [Fact]
    public async Task BindAsync_WhenPublishedMemberServiceIdMissing_ShouldRejectBeforeDispatch()
    {
        var memberService = new RecordingMemberService
        {
            Detail = BuildMemberDetail(
                publishedServiceId: " ",
                lastBoundRevisionId: "revision-existing"),
        };
        var saveAndBindPort = new RecordingSaveAndBindPort();
        var memberCommandPort = new RecordingMemberCommandPort();
        var port = new StudioMemberWorkflowBindingPort(memberService, new StudioWorkflowCapabilityAdmissionTestService(), saveAndBindPort, memberCommandPort);

        var action = () => port.BindAsync(new StudioMemberWorkflowBindingRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            WorkflowYaml: "name: demo\nsteps: []\n"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Studio member 'member-1' is already published but has no published service id.");
        memberService.LastRequest.Should().BeNull();
        memberService.GetBindingRunCallCount.Should().Be(0);
        saveAndBindPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task BindAsync_WhenWorkflowYamlInvalid_ShouldRejectBeforeDispatchingBind()
    {
        var memberService = new RecordingMemberService();
        var admission = new StudioWorkflowCapabilityAdmissionTestService(
            new InvalidOperationException("workflow_yaml is not a valid workflow definition: missing workflow name"));
        var saveAndBindPort = new RecordingSaveAndBindPort();
        var memberCommandPort = new RecordingMemberCommandPort();
        var port = new StudioMemberWorkflowBindingPort(memberService, admission, saveAndBindPort, memberCommandPort);

        var action = () => port.BindAsync(new StudioMemberWorkflowBindingRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            WorkflowYaml: "steps: []\n"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("workflow_yaml is not a valid workflow definition: missing workflow name");
        admission.Requests.Should().ContainSingle()
            .Which.WorkflowYaml.Should().Be("steps: []\n");
        memberService.LastRequest.Should().BeNull();
        saveAndBindPort.LastRequest.Should().BeNull();
    }

    private static StudioMemberDetailResponse BuildMemberDetail(
        string publishedServiceId = "published-service-1",
        string? lastBoundRevisionId = null,
        StudioMemberBindingContractResponse? lastBinding = null,
        string implementationKind = MemberImplementationKindNames.Workflow,
        string memberId = "member-1",
        string scopeId = "scope-1") =>
        new(
            new StudioMemberSummaryResponse(
                MemberId: memberId,
                ScopeId: scopeId,
                DisplayName: "Member One",
                Description: "Member description",
                ImplementationKind: implementationKind,
                LifecycleStage: "active",
                PublishedServiceId: publishedServiceId,
                LastBoundRevisionId: lastBoundRevisionId,
                CreatedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                UpdatedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z")),
            ImplementationRef: null,
            LastBinding: lastBinding);

    private static async Task<string> BindWithoutWorkflowIdAsync(string scopeId, string memberId)
    {
        var memberService = new RecordingMemberService();
        var saveAndBindPort = new RecordingSaveAndBindPort();
        var memberCommandPort = new RecordingMemberCommandPort();
        var port = new StudioMemberWorkflowBindingPort(memberService, new StudioWorkflowCapabilityAdmissionTestService(), saveAndBindPort, memberCommandPort);

        await port.BindAsync(new StudioMemberWorkflowBindingRequest(
            scopeId,
            memberId,
            "name: demo\nsteps: []\n"));

        return memberService.LastRequest!.Workflow!.WorkflowId;
    }

    private sealed class RecordingMemberService : IStudioMemberService
    {
        private readonly Queue<StudioMemberBindingRunStatusResponse> _bindingRuns = new();

        public StudioMemberDetailResponse Detail { get; init; } = BuildMemberDetail();
        public bool ThrowMemberNotFoundOnGet { get; init; }
        public string? LastScopeId { get; private set; }
        public string? LastMemberId { get; private set; }
        public UpdateStudioMemberBindingRequest? LastRequest { get; private set; }
        public int GetBindingRunCallCount { get; private set; }
        public int GetCallCount { get; private set; }
        public int MissingBindingRunReadCount { get; init; }
        private int _missingBindingRunReads;

        public void EnqueueBindingRun(string status, StudioMemberBindingFailureResponse? failure = null) =>
            _bindingRuns.Enqueue(new StudioMemberBindingRunStatusResponse(
                BindingRunId: "bind-run-1",
                ScopeId: "scope-1",
                MemberId: "member-1",
                Status: status,
                StateVersion: 1,
                Failure: failure,
                UpdatedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z")));

        public Task<StudioMemberBindingAcceptedResponse> BindAsync(
            string scopeId,
            string memberId,
            UpdateStudioMemberBindingRequest request,
            CancellationToken ct = default)
        {
            LastScopeId = scopeId;
            LastMemberId = memberId;
            LastRequest = request;
            return Task.FromResult(new StudioMemberBindingAcceptedResponse(
                Status: StudioMemberBindingRunStatusNames.Accepted,
                BindingRunId: "bind-run-1",
                ScopeId: scopeId,
                MemberId: memberId));
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

        public Task<StudioMemberDetailResponse> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default)
        {
            GetCallCount++;
            return ThrowMemberNotFoundOnGet
                ? throw new StudioMemberNotFoundException(scopeId, memberId)
                : Task.FromResult(Detail);
        }

        public Task<StudioMemberBindingViewResponse> GetBindingAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingRunStatusResponse> GetBindingRunAsync(
            string scopeId,
            string memberId,
            string bindingRunId,
            CancellationToken ct = default)
        {
            GetBindingRunCallCount++;
            if (_missingBindingRunReads++ < MissingBindingRunReadCount)
            {
                throw new StudioMemberBindingRunNotFoundException(scopeId, memberId, bindingRunId);
            }

            return Task.FromResult(_bindingRuns.Count == 0
                ? new StudioMemberBindingRunStatusResponse(
                    BindingRunId: bindingRunId,
                    ScopeId: scopeId,
                    MemberId: memberId,
                    Status: StudioMemberBindingRunStatusNames.Succeeded,
                    StateVersion: 1,
                    UpdatedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z"))
                : _bindingRuns.Dequeue());
        }

        public Task<StudioMemberEndpointContractResponse?> GetEndpointContractAsync(
            string scopeId,
            string memberId,
            string endpointId,
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

    private sealed class RecordingMemberCommandPort : IStudioMemberCommandPort
    {
        public string? LastScopeId { get; private set; }
        public string? LastMemberId { get; private set; }
        public StudioMemberPublishedBindingRecordRequest? LastRecordPublishedBinding { get; private set; }

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId,
            CreateStudioMemberRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateImplementationAsync(
            string scopeId,
            string memberId,
            StudioMemberImplementationRefResponse implementation,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RecordPublishedBindingAsync(
            string scopeId,
            string memberId,
            StudioMemberPublishedBindingRecordRequest request,
            CancellationToken ct = default)
        {
            LastScopeId = scopeId;
            LastMemberId = memberId;
            LastRecordPublishedBinding = request;
            return Task.CompletedTask;
        }

        public Task RenameAsync(
            string scopeId,
            string memberId,
            string displayName,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task StartBindingRunAsync(
            StudioMemberBindingRunStartRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task PatchTeamAssignmentAsync(
            string scopeId,
            string memberId,
            string? targetTeamId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingSaveAndBindPort : IScopeWorkflowSaveAndBindPort
    {
        public ScopeWorkflowSaveAndBindRequest? LastRequest { get; private set; }

        public Task<ScopeWorkflowSaveAndBindResult> SaveAndBindAsync(
            ScopeWorkflowSaveAndBindRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            var workflowId = request.WorkflowId ?? "workflow-new";
            return Task.FromResult(new ScopeWorkflowSaveAndBindResult(
                ScopeId: request.ScopeId,
                WorkflowId: workflowId,
                RevisionId: "revision-new",
                Workflow: new ScopeWorkflowUpsertResult(
                    ScopeId: request.ScopeId,
                    WorkflowId: workflowId,
                    ServiceKey: "service-key-1",
                    RevisionId: "revision-new",
                    DefinitionActorIdPrefix: "workflow-definition",
                    ExpectedActorId: "workflow-definition:workflow-new",
                    ExpectedDeploymentId: "deployment-new",
                    AcceptedAtUtc: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                    CommandHandles: [],
                    ReadModelUrl: "/api/scopes/scope-1/workflows/workflow-new"),
                Binding: new ScopeBindingUpsertResult(
                    ScopeId: request.ScopeId,
                    ServiceId: request.ServiceId ?? "published-service-1",
                    DisplayName: request.DisplayName ?? string.Empty,
                    RevisionId: "revision-new",
                    ImplementationKind: ScopeBindingImplementationKind.Workflow,
                    ExpectedActorId: "workflow-definition:workflow-new")));
        }
    }
}
