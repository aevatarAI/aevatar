using Aevatar.AI.ToolProviders.Skills;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.Mainnet.Host.Api.Skills;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using ExternalCapabilityBlocker = Aevatar.Workflow.Abstractions.ExternalCapabilityBlocker;
using ExternalCapabilityExecutionMode = Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode;
using ExternalCapabilityReadiness = Aevatar.Workflow.Abstractions.ExternalCapabilityReadiness;
using ExternalCapabilityReadinessStatus = Aevatar.Workflow.Abstractions.ExternalCapabilityReadinessStatus;
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
        dispatch.Request.CommandIdSeed.Should().MatchRegex("^[0-9a-f]{32}$");
        dispatch.Request.CorrelationIdSeed.Should().Be(dispatch.Request.CommandIdSeed);
    }

    [Fact]
    public async Task InvokeOnceAsync_ShouldUseDistinctCommandIdentityForEachInvocation()
    {
        var dispatch = new RecordingWorkflowChatDispatch();
        var service = new UserSkillRunService(
            new RecordingRemoteSkillFetcher(WorkflowSkill()),
            dispatch,
            new UnusedScheduleProvisioningPort(),
            new NoOpSkillWorkflowConfirmationPort());
        var callerCredential = SourceReadableCallerCredential();

        await service.InvokeOnceAsync(
            "skill-alpha",
            callerCredential,
            "scope-alpha",
            "first run",
            CancellationToken.None);
        var firstCommandIdentity = dispatch.Request!.CommandIdSeed;

        await service.InvokeOnceAsync(
            "skill-alpha",
            callerCredential,
            "scope-alpha",
            "second run",
            CancellationToken.None);

        dispatch.Request!.CommandIdSeed.Should().MatchRegex("^[0-9a-f]{32}$");
        dispatch.Request.CommandIdSeed.Should().NotBe(firstCommandIdentity);
        dispatch.Request.CorrelationIdSeed.Should().Be(dispatch.Request.CommandIdSeed);
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
        outcome.Receipt.Should().NotBeNull();
        outcome.Receipt!.ScheduleId.Should().Be("schedule-alpha");
        outcome.Receipt.ScopeId.Should().Be("scope-alpha");
        outcome.Receipt.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Accepted);
        outcome.Receipt.BindingRunId.Should().Be("bind-alpha");
        outcome.Receipt.ScheduleProvisioningId.Should().Be("provision-alpha");
        outcome.Receipt.ScheduleProvisioningStatus.Should().Be("succeeded");
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
        schedule.Request.ProvisioningBearerToken.Should().Be("caller-token");
        schedule.Request.AuthenticatedOwner.Should().NotBeNull();
        schedule.Request.AuthenticatedOwner!.SubjectExternalUserId.Should().Be("nyx-user-alpha");
        schedule.Request.AuthenticatedOwner.VerifiedBindingId.Should().Be("binding-alpha");
        schedule.Request.CapabilityAdmission.Should().NotBeNull();
        schedule.Request.CapabilityAdmission!.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Durable);
        schedule.Request.CapabilityAdmission.CallerId.Should().Be("nyx-user-alpha");
        schedule.Request.CapabilityAdmission.NyxIdCallerCredential!.Kind
            .Should().Be(NyxIdCallerCredentialKind.SourceReadableUserBearer);
        var explicitConfirmation = schedule.Request.CapabilityAdmission.ExplicitRequestConfirmations
            .Should().ContainSingle().Which;
        explicitConfirmation.WorkflowId.Should().BeEmpty();
        explicitConfirmation.RevisionId.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduleAsync_WhenProvisioningIsPending_ShouldReturnTypedReceiptWithoutScheduleId()
    {
        var schedule = new RecordingScheduleProvisioningPort
        {
            Result = new WorkflowScheduleProvisioningResult(
                "m-alpha",
                "scope-alpha",
                "team-alpha",
                ProvisionWorkflowBindingStatusNames.Accepted,
                "/admin#/observatory",
                "/studio/member")
            {
                ScheduleId = null,
                BindingRunId = "bind-alpha",
                ScheduleProvisioningId = "provision-alpha",
                ScheduleProvisioningStatus = "pending_binding",
            },
        };
        var service = new UserSkillRunService(
            new RecordingRemoteSkillFetcher(WorkflowSkill()),
            new RecordingWorkflowChatDispatch(),
            schedule,
            new RecordingWorkflowConfirmationPort(ConfirmedWorkflow()));

        var outcome = await service.ScheduleAsync(
            "skill-alpha",
            SourceReadableCallerCredential(),
            "scope-alpha",
            "run the check",
            "*/15 * * * *",
            "UTC",
            "Codex Check",
            "team-alpha",
            "sha256:reviewed",
            CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        outcome.Receipt.Should().NotBeNull();
        outcome.Receipt!.ScheduleId.Should().BeNull();
        outcome.Receipt.MemberId.Should().Be("m-alpha");
        outcome.Receipt.ScopeId.Should().Be("scope-alpha");
        outcome.Receipt.TeamId.Should().Be("team-alpha");
        outcome.Receipt.BindingStatus.Should().Be(ProvisionWorkflowBindingStatusNames.Accepted);
        outcome.Receipt.BindingRunId.Should().Be("bind-alpha");
        outcome.Receipt.ScheduleProvisioningId.Should().Be("provision-alpha");
        outcome.Receipt.ScheduleProvisioningStatus.Should().Be("pending_binding");
    }

    [Fact]
    public async Task ScheduleAsync_WhenCredentialOperationIsInProgress_ShouldReturnTypedConflict()
    {
        var schedule = new RecordingScheduleProvisioningPort
        {
            Exception = new ScheduledDispatchConflictException(
                "schedule-sensitive",
                "team_automation_operation_in_progress"),
        };
        var service = new UserSkillRunService(
            new RecordingRemoteSkillFetcher(WorkflowSkill()),
            new RecordingWorkflowChatDispatch(),
            schedule,
            new RecordingWorkflowConfirmationPort(ConfirmedWorkflow()));

        var outcome = await service.ScheduleAsync(
            "skill-alpha",
            SourceReadableCallerCredential(),
            "scope-alpha",
            "run the check",
            "*/15 * * * *",
            "UTC",
            "Codex Check",
            "team-alpha",
            "sha256:reviewed",
            CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.ErrorCode.Should().Be("conflict");
        outcome.ErrorMessage.Should().Contain("still in progress");
        outcome.ErrorMessage.Should().NotContain("schedule-sensitive");
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

    [Fact]
    public async Task ScheduleAsync_WhenRequiredRouteIsUnresolved_ShouldReturnRepairableFailure()
    {
        var schedule = new RecordingScheduleProvisioningPort
        {
            Exception = new StudioMemberAutomationCatalogRouteUnresolvedException(
                ["service-alpha"]),
        };
        var service = new UserSkillRunService(
            new RecordingRemoteSkillFetcher(WorkflowSkill()),
            new RecordingWorkflowChatDispatch(),
            schedule,
            new RecordingWorkflowConfirmationPort(ConfirmedWorkflow()));

        var outcome = await service.ScheduleAsync(
            "skill-alpha",
            SourceReadableCallerCredential(),
            "scope-alpha",
            "run the check",
            "*/15 * * * *",
            "UTC",
            "Codex Check",
            "team-alpha",
            "sha256:reviewed",
            CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.ErrorCode.Should().Be("schedule_authorization_route_unresolved");
        outcome.ErrorMessage.Should().Be(
            "NyxID could not resolve a configured route required by this workflow. " +
            "Repair or deactivate the route before retrying.");
        outcome.RequiredUserServiceIds.Should().Equal("service-alpha");
    }

    [Fact]
    public async Task ScheduleAsync_ShouldReturnSafeAdmissionBlockerCode()
    {
        var blockerCode = "NYXID_EXPLICIT_REQUEST_CONFIRMATION_BINDING_MISMATCH";
        var schedule = new RecordingScheduleProvisioningPort
        {
            Exception = new WorkflowExternalCapabilityAdmissionException(new ExternalCapabilityReadiness
            {
                Status = ExternalCapabilityReadinessStatus.ContractDrift,
                Blockers =
                {
                    new ExternalCapabilityBlocker
                    {
                        Status = ExternalCapabilityReadinessStatus.ContractDrift,
                        Code = blockerCode,
                        SafeMessage = "The confirmation is bound to another workflow identity.",
                    },
                },
            }),
        };
        var service = new UserSkillRunService(
            new RecordingRemoteSkillFetcher(WorkflowSkill()),
            new RecordingWorkflowChatDispatch(),
            schedule,
            new RecordingWorkflowConfirmationPort(ConfirmedWorkflow()));

        var outcome = await service.ScheduleAsync(
            "skill-alpha",
            SourceReadableCallerCredential(),
            "scope-alpha",
            "run the check",
            "*/15 * * * *",
            "UTC",
            "Codex Check",
            "team-alpha",
            "sha256:reviewed",
            CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.ErrorCode.Should().Be(blockerCode);
        outcome.ErrorMessage.Should().Be("The confirmation is bound to another workflow identity.");
    }

    [Theory]
    [InlineData(
        "api_key_scope_plan_denied",
        "NyxID denied the requested Agent Key scope for this caller.")]
    [InlineData(
        "scheduled_credential_recovery_evidence_missing",
        "The scheduled Agent Key could not be issued.")]
    public async Task ScheduleAsync_WhenCredentialMaterializationFails_ShouldPreserveTypedProviderFailure(
        string failureCode,
        string expectedMessage)
    {
        var schedule = new RecordingScheduleProvisioningPort
        {
            Exception = new StudioScheduledCredentialMaterializationException(
                failureCode,
                effectsCleaned: !string.Equals(
                    failureCode,
                    "scheduled_credential_recovery_evidence_missing",
                    StringComparison.Ordinal),
                new InvalidOperationException(failureCode),
                recoveryBlocked: string.Equals(
                    failureCode,
                    "scheduled_credential_recovery_evidence_missing",
                    StringComparison.Ordinal),
                failureCode: failureCode),
        };
        var service = new UserSkillRunService(
            new RecordingRemoteSkillFetcher(WorkflowSkill()),
            new RecordingWorkflowChatDispatch(),
            schedule,
            new RecordingWorkflowConfirmationPort(ConfirmedWorkflow()));

        var outcome = await service.ScheduleAsync(
            "skill-alpha",
            SourceReadableCallerCredential(),
            "scope-alpha",
            "run the check",
            "*/15 * * * *",
            "UTC",
            "Codex Check",
            "team-alpha",
            "sha256:reviewed",
            CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.ErrorCode.Should().Be(failureCode);
        outcome.ErrorMessage.Should().Be(expectedMessage);
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
        public WorkflowScheduleProvisioningResult? Result { get; init; }

        public Task<WorkflowScheduleProvisioningResult> ProvisionAsync(
            WorkflowScheduleProvisioningRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            if (Exception != null) throw Exception;
            return Task.FromResult(Result ?? new WorkflowScheduleProvisioningResult(
                "member-alpha",
                request.ScopeId,
                request.TeamId,
                ProvisionWorkflowBindingStatusNames.Accepted,
                "/admin#/observatory",
                "/studio/member")
            {
                ScheduleId = "schedule-alpha",
                BindingRunId = "bind-alpha",
                ScheduleProvisioningId = "provision-alpha",
                ScheduleProvisioningStatus = "succeeded",
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
