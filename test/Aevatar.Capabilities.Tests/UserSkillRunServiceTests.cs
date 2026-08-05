using Aevatar.AI.ToolProviders.Skills;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Mainnet.Host.Api.Skills;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using ExternalCapabilityExecutionMode = Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode;
using NyxIdCallerCredentialKind = Aevatar.Workflow.Abstractions.NyxIdCallerCredentialKind;
using NyxIdOperationRisk = Aevatar.Workflow.Abstractions.NyxIdOperationRisk;

namespace Aevatar.Capabilities.Tests;

public sealed class UserSkillRunServiceTests
{
    [Fact]
    public async Task InvokeOnceAsync_ShouldUseNormalizedBearerAndPreserveCompleteCallerCredential()
    {
        var fetcher = new RecordingRemoteSkillFetcher(WorkflowSkill());
        var dispatch = new RecordingWorkflowChatDispatch();
        var service = new UserSkillRunService(
            fetcher,
            dispatch,
            new UnusedScheduleProvisioningPort(),
            new NoOpSkillWorkflowConfirmationPort());
        var callerCredential = new WorkflowCallerCredential(
            "  caller-token  ",
            new WorkflowCallerNyxIdAuthority(
                "nyxid",
                string.Empty,
                "nyx-user-alpha",
                "proxy",
                "binding-alpha"));

        var outcome = await service.InvokeOnceAsync(
            "skill-alpha",
            callerCredential,
            "scope-alpha",
            "run the check",
            CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        fetcher.AccessToken.Should().Be("caller-token");
        fetcher.SkillGuid.Should().Be("skill-alpha");
        dispatch.Request.Should().NotBeNull();
        dispatch.Request!.ScopeId.Should().Be("scope-alpha");
        dispatch.Request.CallerCredential.Should().BeSameAs(callerCredential);
        dispatch.Request.CallerCredential!.NyxIdAuthority!.ExternalUserId.Should().Be("nyx-user-alpha");
        dispatch.Request.CallerCredential.NyxIdAuthority.BindingId.Should().Be("binding-alpha");
    }

    [Fact]
    public async Task ScheduleAsync_ShouldForwardAuthenticatedOwnerAndProvisioningBearer()
    {
        var fetcher = new RecordingRemoteSkillFetcher(WorkflowSkill());
        var dispatch = new RecordingWorkflowChatDispatch();
        var schedule = new RecordingScheduleProvisioningPort();
        var confirmation = new RecordingWorkflowConfirmationPort(ConfirmedWorkflow());
        var service = new UserSkillRunService(fetcher, dispatch, schedule, confirmation);
        var callerCredential = new WorkflowCallerCredential(
            "  delegation-token  ",
            new WorkflowCallerNyxIdAuthority(
                "nyxid",
                string.Empty,
                "nyx-user-alpha",
                "proxy",
                "binding-alpha"),
            NyxIdCallerCredentialKind.ProxyDelegation,
            "  caller-token  ");

        var outcome = await service.ScheduleAsync(
            "skill-alpha",
            callerCredential,
            "scope-alpha",
            "run the check",
            "*/15 * * * *",
            "Asia/Shanghai",
            "Codex Check",
            "team-alpha",
            "sha256:reviewed",
            CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        fetcher.AccessToken.Should().Be("delegation-token");
        confirmation.Request.Should().NotBeNull();
        confirmation.Request!.SourceReadableNyxIdAccessToken.Should().Be("caller-token");
        confirmation.Request.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Durable);
        confirmation.Request.ConfirmationToken.Should().Be("sha256:reviewed");
        schedule.Request.Should().NotBeNull();
        schedule.Request!.ScopeId.Should().Be("scope-alpha");
        schedule.Request.TeamId.Should().Be("team-alpha");
        schedule.Request.DisplayName.Should().Be("Codex Check");
        schedule.Request.ScheduleCron.Should().Be("*/15 * * * *");
        schedule.Request.ScheduleTimezone.Should().Be("Asia/Shanghai");
        schedule.Request.RunImmediately.Should().BeFalse();
        schedule.Request.ProvisioningBearerToken.Should().Be("delegation-token");
        schedule.Request.AuthenticatedOwner.Should().NotBeNull();
        schedule.Request.AuthenticatedOwner!.SubjectExternalUserId.Should().Be("nyx-user-alpha");
        schedule.Request.AuthenticatedOwner.VerifiedBindingId.Should().Be("binding-alpha");
        schedule.Request.CapabilityAdmission.Should().NotBeNull();
        schedule.Request.CapabilityAdmission!.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Durable);
        schedule.Request.CapabilityAdmission.CallerId.Should().Be("nyx-user-alpha");
        schedule.Request.CapabilityAdmission.NyxIdCallerCredential!.Kind
            .Should().Be(NyxIdCallerCredentialKind.SourceReadableUserBearer);
        schedule.Request.CapabilityAdmission.ExplicitRequestConfirmations.Should().ContainSingle();
    }

    [Fact]
    public async Task ScheduleAsync_WithoutConfirmationToken_ShouldReturnPreviewWithoutProvisioning()
    {
        var fetcher = new RecordingRemoteSkillFetcher(WorkflowSkill());
        var schedule = new RecordingScheduleProvisioningPort();
        var confirmation = new RecordingWorkflowConfirmationPort(ConfirmationRequiredWorkflow());
        var service = new UserSkillRunService(
            fetcher,
            new RecordingWorkflowChatDispatch(),
            schedule,
            confirmation);

        var outcome = await service.ScheduleAsync(
            "skill-alpha",
            SourceReadableCallerCredential(),
            "scope-alpha",
            "run the check",
            "*/15 * * * *",
            "UTC",
            "Codex Check",
            "team-alpha",
            string.Empty,
            CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Confirmation.Should().NotBeNull();
        outcome.Confirmation!.Status.Should().Be("confirmation_required");
        outcome.Confirmation.ConfirmationToken.Should().Be("sha256:reviewed");
        outcome.Confirmation.Workflows.Should().ContainSingle();
        confirmation.Request!.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Durable);
        schedule.Request.Should().BeNull();
    }

    [Fact]
    public async Task ScheduleAsync_WithoutSourceReadableCredential_ShouldFailBeforeSkillFetchOrProvisioning()
    {
        var fetcher = new RecordingRemoteSkillFetcher(WorkflowSkill());
        var schedule = new RecordingScheduleProvisioningPort();
        var confirmation = new RecordingWorkflowConfirmationPort(ConfirmedWorkflow());
        var service = new UserSkillRunService(
            fetcher,
            new RecordingWorkflowChatDispatch(),
            schedule,
            confirmation);
        var callerCredential = new WorkflowCallerCredential(
            "delegation-token",
            new WorkflowCallerNyxIdAuthority(
                "nyxid",
                string.Empty,
                "nyx-user-alpha",
                "proxy",
                "binding-alpha"),
            NyxIdCallerCredentialKind.ProxyDelegation);

        var outcome = await service.ScheduleAsync(
            "skill-alpha",
            callerCredential,
            "scope-alpha",
            "run the check",
            "*/15 * * * *",
            "UTC",
            "Codex Check",
            "team-alpha",
            "sha256:reviewed",
            CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.ErrorCode.Should().Be("source_readable_caller_credential_required");
        fetcher.InvocationCount.Should().Be(0);
        confirmation.Request.Should().BeNull();
        schedule.Request.Should().BeNull();
    }

    [Fact]
    public async Task ScheduleAsync_WithMultipleRootWorkflows_ShouldRejectBeforeConfirmationOrProvisioning()
    {
        var skill = WorkflowSkill(
            new SkillWorkflowDescriptor
            {
                WorkflowId = "workflow-alpha",
                WorkflowYamls = ["name: workflow-alpha\nsteps: []\n"],
            },
            new SkillWorkflowDescriptor
            {
                WorkflowId = "workflow-beta",
                WorkflowYamls = ["name: workflow-beta\nsteps: []\n"],
            });
        var confirmation = new RecordingWorkflowConfirmationPort(ConfirmedWorkflow());
        var schedule = new RecordingScheduleProvisioningPort();
        var service = new UserSkillRunService(
            new RecordingRemoteSkillFetcher(skill),
            new RecordingWorkflowChatDispatch(),
            schedule,
            confirmation);

        var outcome = await service.ScheduleAsync(
            "skill-alpha",
            SourceReadableCallerCredential(),
            "scope-alpha",
            "run the check",
            "*/15 * * * *",
            "UTC",
            "Codex Check",
            "team-alpha",
            string.Empty,
            CancellationToken.None);

        outcome.ErrorCode.Should().Be("skill_schedule_workflow_ambiguous");
        confirmation.Request.Should().BeNull();
        schedule.Request.Should().BeNull();
    }

    [Fact]
    public async Task ScheduleAsync_WithSubWorkflowBundle_ShouldRejectBeforeConfirmationOrProvisioning()
    {
        var skill = WorkflowSkill(new SkillWorkflowDescriptor
        {
            WorkflowId = "workflow-bundle",
            WorkflowYamls =
            [
                "name: workflow-root\nsteps: []\n",
                "name: workflow-child\nsteps: []\n",
            ],
        });
        var confirmation = new RecordingWorkflowConfirmationPort(ConfirmedWorkflow());
        var schedule = new RecordingScheduleProvisioningPort();
        var service = new UserSkillRunService(
            new RecordingRemoteSkillFetcher(skill),
            new RecordingWorkflowChatDispatch(),
            schedule,
            confirmation);

        var outcome = await service.ScheduleAsync(
            "skill-alpha",
            SourceReadableCallerCredential(),
            "scope-alpha",
            "run the check",
            "*/15 * * * *",
            "UTC",
            "Codex Check",
            "team-alpha",
            string.Empty,
            CancellationToken.None);

        outcome.ErrorCode.Should().Be("skill_schedule_workflow_bundle_unsupported");
        confirmation.Request.Should().BeNull();
        schedule.Request.Should().BeNull();
    }

    [Fact]
    public async Task ScheduleAsync_ShouldReturnFailure_WhenAuthorizationProjectionIsPending()
    {
        var fetcher = new RecordingRemoteSkillFetcher(WorkflowSkill());
        var dispatch = new RecordingWorkflowChatDispatch();
        var schedule = new RecordingScheduleProvisioningPort
        {
            Exception = new StudioMemberAutomationProjectionPendingException(23),
        };
        var service = new UserSkillRunService(
            fetcher,
            dispatch,
            schedule,
            new RecordingWorkflowConfirmationPort(ConfirmedWorkflow()));
        var callerCredential = SourceReadableCallerCredential();

        var outcome = await service.ScheduleAsync(
            "skill-alpha",
            callerCredential,
            "scope-alpha",
            "run the check",
            "*/15 * * * *",
            "UTC",
            "Codex Check",
            "team-alpha",
            "sha256:reviewed",
            CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.ErrorCode.Should().Be("schedule_authorization_projection_pending");
        outcome.ErrorMessage.Should().Contain("Required state version: 23");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("token with spaces")]
    public async Task InvokeOnceAsync_WhenBearerIsInvalid_ShouldFailBeforeExternalCalls(string? bearerToken)
    {
        var fetcher = new RecordingRemoteSkillFetcher(WorkflowSkill());
        var dispatch = new RecordingWorkflowChatDispatch();
        var service = new UserSkillRunService(
            fetcher,
            dispatch,
            new UnusedScheduleProvisioningPort(),
            new NoOpSkillWorkflowConfirmationPort());

        var outcome = await service.InvokeOnceAsync(
            "skill-alpha",
            new WorkflowCallerCredential(bearerToken),
            "scope-alpha",
            "run the check",
            CancellationToken.None);

        outcome.Should().Be(SkillRunOutcome.Failed(
            "invalid_caller_credential",
            "Caller credential is invalid."));
        fetcher.InvocationCount.Should().Be(0);
        dispatch.Request.Should().BeNull();
    }

    private static SkillDefinition WorkflowSkill(params SkillWorkflowDescriptor[] workflows) =>
        new()
        {
            Name = "codex-check",
            Description = "Run a managed Codex check.",
            Instructions = "Return CODEX_EXEC_READY.",
            Source = SkillSource.Remote,
            Workflows = workflows.Length > 0
                ? workflows
                : [
                    new SkillWorkflowDescriptor
                    {
                        WorkflowId = "codex-check",
                        WorkflowYamls = ["name: codex-check\nsteps: []\n"],
                    },
                ],
        };

    private static WorkflowCallerCredential SourceReadableCallerCredential() =>
        new(
            "caller-token",
            new WorkflowCallerNyxIdAuthority(
                "nyxid",
                string.Empty,
                "nyx-user-alpha",
                "proxy",
                "binding-alpha"),
            NyxIdCallerCredentialKind.SourceReadableUserBearer);

    private static SkillWorkflowConfirmationResult ConfirmationRequiredWorkflow() =>
        new(
            "confirmation_required",
            Confirmed: false,
            ConfirmationRequests: [WorkflowPreview()],
            ConfirmationToken: "sha256:reviewed");

    private static SkillWorkflowConfirmationResult ConfirmedWorkflow() =>
        new(
            "confirmed",
            Confirmed: true,
            ConfirmationRequests: [WorkflowPreview()],
            ConfirmationToken: "sha256:reviewed");

    private static SkillWorkflowMountPreview WorkflowPreview()
    {
        var explicitRequest = new SkillWorkflowExplicitRequestConfirmation(
            "codex-check/step-1",
            "sha256:request",
            NyxIdOperationRisk.ReadOnly);
        return new SkillWorkflowMountPreview(
            "codex-check",
            "rev-codex-check",
            "sha256:bundle",
            [],
            new SkillWorkflowMountConfirmation(
                "codex-check",
                "rev-codex-check",
                "sha256:bundle",
                [explicitRequest]));
    }

    private sealed class RecordingRemoteSkillFetcher(SkillDefinition skill) : IRemoteSkillFetcher
    {
        public int InvocationCount { get; private set; }

        public string? AccessToken { get; private set; }

        public string? SkillGuid { get; private set; }

        public Task<SkillDefinition?> FetchSkillAsync(
            string accessToken,
            string nameOrId,
            CancellationToken ct = default)
        {
            InvocationCount++;
            AccessToken = accessToken;
            SkillGuid = nameOrId;
            return Task.FromResult<SkillDefinition?>(skill);
        }
    }

    private sealed class RecordingWorkflowChatDispatch :
        ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public WorkflowChatRunRequest? Request { get; private set; }

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            Request = command;
            return Task.FromResult(CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt(
                    "run-alpha",
                    "codex-check",
                    "command-alpha",
                    "correlation-alpha")));
        }
    }

    private sealed class RecordingWorkflowConfirmationPort(SkillWorkflowConfirmationResult result) :
        ISkillWorkflowConfirmationPort
    {
        public SkillWorkflowConfirmationRequest? Request { get; private set; }

        public Task<SkillWorkflowConfirmationResult> ConfirmAsync(
            SkillWorkflowConfirmationRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingScheduleProvisioningPort : IWorkflowScheduleProvisioningPort
    {
        public WorkflowScheduleProvisioningRequest? Request { get; private set; }
        public Exception? Exception { get; init; }

        public Task<WorkflowScheduleProvisioningResult> ProvisionAsync(
            WorkflowScheduleProvisioningRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            if (Exception != null) throw Exception;
            return Task.FromResult(new WorkflowScheduleProvisioningResult(
                "member-alpha",
                request.ScopeId,
                request.TeamId,
                ProvisionWorkflowBindingStatusNames.Accepted,
                "/admin#/observatory",
                "/studio/member")
            {
                ScheduleId = "schedule-alpha",
            });
        }
    }

    private sealed class UnusedScheduleProvisioningPort : IWorkflowScheduleProvisioningPort
    {
        public Task<WorkflowScheduleProvisioningResult> ProvisionAsync(
            WorkflowScheduleProvisioningRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This test exercises one-shot skill invocation only.");
    }
}
