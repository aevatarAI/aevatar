using System.Text.Json;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Delivery;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowDeliveryProvisioningExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_WhenAcceptedReplicaSurvivesRestart_ShouldRemintAndResumeProvisioning()
    {
        var provisioning = new RecordingProvisioningService();
        var commands = new RecordingCommandPort();
        var tokens = new RecordingAccessTokenProvider();
        var executor = NewExecutor(provisioning, commands, tokens);
        var delivery = Delivery(WorkflowInstallationStatus.Accepted, attempt: 2);

        var result = await executor.ExecuteAsync(delivery);

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.ProvisioningAccepted);
        tokens.Authorities.Should().ContainSingle();
        tokens.Authorities[0].BindingId.Should().Be("binding-alpha");
        provisioning.Requests.Should().ContainSingle();
        var request = provisioning.Requests[0];
        request.CapabilityAdmission!.ExistingPlan!.AdmissionDigest.Should()
            .Be(delivery.Installation!.CapabilityAdmissionPlan.AdmissionDigest);
        request.CapabilityAdmission.NyxIdCallerCredential.Should().NotBeNull();
        request.ProvisioningBearerToken.Should().Be("token-alpha");
        request.ScheduleOperationId.Should().Be("installation-alpha:provision:a2");
        request.Prompt.Should().NotBeNullOrWhiteSpace();
        using (var prompt = JsonDocument.Parse(request.Prompt))
        {
            prompt.RootElement.GetProperty("period_label").GetString().Should().Be("2026-W33");
            prompt.RootElement.GetProperty("run_date").GetString().Should().Be("2026-08-16");
            prompt.RootElement.GetProperty("submit").GetBoolean().Should().BeFalse();
        }
        commands.ProvisioningAccepted.Should().ContainSingle();
        commands.ProvisioningAccepted[0].Attempt.Should().Be(2);
        commands.ProvisioningAccepted[0].OperationId.Should().Be("installation-alpha:provision:a2");
        commands.Failed.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkflowDeliveryTriggerKind.OneShot)]
    [InlineData(WorkflowDeliveryTriggerKind.Cron)]
    public async Task ExecuteAsync_WhenScheduledAttemptTwoIsAccepted_ShouldForwardNewProvisioningIntent(
        WorkflowDeliveryTriggerKind triggerKind)
    {
        var provisioning = new RecordingProvisioningService
        {
            ResponseScheduleProvisioningId = "schedule-provisioning-attempt-2",
        };
        var commands = new RecordingCommandPort();
        var executor = NewExecutor(provisioning, commands, new RecordingAccessTokenProvider());
        var delivery = RetriedDelivery(triggerKind);

        var result = await executor.ExecuteAsync(delivery);

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.ProvisioningAccepted);
        var request = provisioning.Requests.Should().ContainSingle().Which;
        request.ScheduleOperationId.Should().Be("installation-alpha:provision:a2");
        request.ScheduleIdempotencyKey.Should().Be("publish-alpha");
        request.RunImmediately.Should().Be(triggerKind == WorkflowDeliveryTriggerKind.OneShot);
        request.Cron.Should().Be(
            triggerKind == WorkflowDeliveryTriggerKind.Cron ? "0 9 * * 1-5" : null);
        var accepted = commands.ProvisioningAccepted.Should().ContainSingle().Which;
        accepted.Attempt.Should().Be(2);
        accepted.OperationId.Should().Be("installation-alpha:provision:a2");
        accepted.ScheduleId.Should().Be("schedule-alpha");
        accepted.ScheduleProvisioningId.Should().Be("schedule-provisioning-attempt-2");
        accepted.ScheduleProvisioningStatus.Should().Be("pending_binding");
    }

    [Fact]
    public async Task ExecuteAsync_WhenProvisioningFails_ShouldPersistSafeFencedFailureForRetry()
    {
        var provisioning = new RecordingProvisioningService
        {
            Failure = new InvalidOperationException("Bearer token-alpha must never be persisted"),
        };
        var commands = new RecordingCommandPort();
        var executor = NewExecutor(provisioning, commands, new RecordingAccessTokenProvider());
        var delivery = Delivery(WorkflowInstallationStatus.Accepted, attempt: 3);

        var result = await executor.ExecuteAsync(delivery);

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.Failed);
        result.ErrorCode.Should().Be("PROVISIONING_FAILED");
        commands.ProvisioningAccepted.Should().BeEmpty();
        commands.Failed.Should().ContainSingle();
        var failed = commands.Failed[0];
        failed.Attempt.Should().Be(3);
        failed.OperationId.Should().Be("installation-alpha:provision:a3");
        failed.ExpectedStatus.Should().Be(WorkflowInstallationStatus.Accepted);
        failed.ErrorMessage.Should().Be("Workflow provisioning failed.");
        failed.ErrorMessage.Should().NotContain("token-alpha");
    }

    [Fact]
    public async Task ExecuteAsync_WhenAcceptedOutcomeDispatchIsUncertain_ShouldNotPersistFailure()
    {
        var provisioning = new RecordingProvisioningService();
        var commands = new RecordingCommandPort
        {
            ProvisioningAcceptedFailure = new TimeoutException("accepted outcome ACK was not observed"),
        };
        var executor = NewExecutor(provisioning, commands, new RecordingAccessTokenProvider());
        var delivery = Delivery(WorkflowInstallationStatus.Accepted);

        var execute = () => executor.ExecuteAsync(delivery);

        await execute.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*ACK was not observed*");
        provisioning.Requests.Should().ContainSingle();
        commands.ProvisioningAccepted.Should().ContainSingle();
        commands.Failed.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkflowDeliveryAcceptancePolicies.BudgetVarianceMonitor)]
    [InlineData(WorkflowDeliveryAcceptancePolicies.AttendanceFillReminder)]
    [InlineData(WorkflowDeliveryAcceptancePolicies.MonthlyAttendanceApproval)]
    public async Task ExecuteAsync_WhenPackageSupportsAutomaticAcceptance_ShouldUsePackageSpecificPreviewInput(
        string workflowName)
    {
        var provisioning = new RecordingProvisioningService();
        var executor = NewExecutor(
            provisioning,
            new RecordingCommandPort(),
            new RecordingAccessTokenProvider());
        var delivery = Delivery(
            WorkflowInstallationStatus.Accepted,
            workflowName: workflowName);

        var result = await executor.ExecuteAsync(delivery);

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.ProvisioningAccepted);
        var request = provisioning.Requests.Should().ContainSingle().Which;
        request.Prompt.Should().NotBeNullOrWhiteSpace();
        using var prompt = JsonDocument.Parse(request.Prompt);
        var root = prompt.RootElement;
        root.GetProperty("submit").GetBoolean().Should().BeFalse();
        switch (workflowName)
        {
            case WorkflowDeliveryAcceptancePolicies.BudgetVarianceMonitor:
                root.GetProperty("period_label").GetString().Should().Be("2026-W33");
                root.GetProperty("run_date").GetString().Should().Be("2026-08-16");
                break;
            case WorkflowDeliveryAcceptancePolicies.AttendanceFillReminder:
                root.GetProperty("period_label").GetString().Should().Be("2026-08");
                root.GetProperty("days_left").GetString().Should().Be("0");
                break;
            case WorkflowDeliveryAcceptancePolicies.MonthlyAttendanceApproval:
                root.GetProperty("period_label").GetString().Should().Be("2026-08");
                root.GetProperty("run_date").GetString().Should().Be("2026-08-16");
                root.GetProperty("month_end_only").GetBoolean().Should().BeFalse();
                break;
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenOnboardingPackageIsAccepted_ShouldUseDurableOwnerInSafeInlinePreviewInput()
    {
        var provisioning = new RecordingProvisioningService();
        var executor = NewExecutor(
            provisioning,
            new RecordingCommandPort(),
            new RecordingAccessTokenProvider());
        var delivery = Delivery(
            WorkflowInstallationStatus.Accepted,
            workflowName: WorkflowDeliveryAcceptancePolicies.OnboardingEmailApproval);

        await executor.ExecuteAsync(delivery);

        var request = provisioning.Requests.Should().ContainSingle().Which;
        using var prompt = JsonDocument.Parse(request.Prompt);
        var root = prompt.RootElement;
        root.GetProperty("lark_name").GetString().Should().Be("Aevatar Delivery Preview");
        root.GetProperty("department").GetString().Should().Be("Workflow Delivery");
        root.GetProperty("email_username").GetString().Should().Be("aevatar.delivery.preview.20260816");
        root.GetProperty("operator_id").GetString().Should().Be("user-alpha");
        root.GetProperty("run_date").GetString().Should().Be("2026-08-16");
        root.GetProperty("onboarding_date").GetString().Should().Be("2026-08-23");
        root.GetProperty("submit").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenInvoiceIsPublishedWithoutAutomaticRun_ShouldKeepSafePreviewInput()
    {
        var provisioning = new RecordingProvisioningService();
        var executor = NewExecutor(
            provisioning,
            new RecordingCommandPort(),
            new RecordingAccessTokenProvider());
        var delivery = Delivery(
            WorkflowInstallationStatus.Accepted,
            workflowName: WorkflowDeliveryAcceptancePolicies.InvoicePrecheckApproval);
        delivery = delivery with
        {
            Installation = delivery.Installation! with
            {
                TriggerIntent = new WorkflowDeliveryTriggerIntent(
                    WorkflowDeliveryTriggerKind.None,
                    null,
                    null,
                    false),
            },
        };

        var result = await executor.ExecuteAsync(delivery);

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.ProvisioningAccepted);
        var request = provisioning.Requests.Should().ContainSingle().Which;
        request.RunImmediately.Should().BeFalse();
        request.Cron.Should().BeNull();
        using var prompt = JsonDocument.Parse(request.Prompt);
        prompt.RootElement.GetProperty("run_date").GetString().Should().Be("2026-08-16");
        prompt.RootElement.GetProperty("submit").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData(WorkflowDeliveryAcceptancePolicies.InvoicePrecheckApproval, "AUTOMATIC_ACCEPTANCE_UNSUPPORTED")]
    [InlineData("unknown_delivery_workflow", "UNSUPPORTED_DELIVERY_PACKAGE")]
    public async Task ExecuteAsync_WhenPackageCannotRunAutomaticAcceptance_ShouldFailClosed(
        string workflowName,
        string expectedCode)
    {
        var provisioning = new RecordingProvisioningService();
        var commands = new RecordingCommandPort();
        var tokens = new RecordingAccessTokenProvider();
        var executor = NewExecutor(provisioning, commands, tokens);
        var delivery = Delivery(
            WorkflowInstallationStatus.Accepted,
            workflowName: workflowName);

        var result = await executor.ExecuteAsync(delivery);

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.Failed);
        result.ErrorCode.Should().Be(expectedCode);
        provisioning.Requests.Should().BeEmpty();
        tokens.Authorities.Should().BeEmpty();
        commands.Failed.Should().ContainSingle().Which.ErrorCode.Should().Be(expectedCode);
    }

    private static WorkflowDeliveryProvisioningExecutor NewExecutor(
        IStudioWorkflowProvisioningService provisioning,
        IWorkflowDeliveryCommandPort commands,
        IWorkflowCallerAccessTokenProvider tokens) =>
        new(
            provisioning,
            commands,
            tokens,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T04:00:00Z")),
            NullLogger<WorkflowDeliveryProvisioningExecutor>.Instance);

    internal static WorkflowDeliverySnapshot Delivery(
        WorkflowInstallationStatus status,
        int attempt = 1,
        string? pageSuffix = null,
        string workflowName = WorkflowDeliveryAcceptancePolicies.BudgetVarianceMonitor)
    {
        var suffix = pageSuffix ?? "alpha";
        var now = DateTimeOffset.Parse("2026-08-16T03:00:00Z");
        var yaml = $"name: {workflowName}\nsteps: []\n";
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            null,
            ExternalCapabilityExecutionMode.Durable,
            [],
            [],
            new ExternalCapabilityAuthorizationOwner
            {
                Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
                OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
                OwnerSubject = "caller-alpha",
            });
        var owner = new AuthenticatedAuthorizationOwnerContext(
            new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = "caller-alpha",
            },
            "nyxid",
            string.Empty,
            "user-alpha",
            "binding-alpha");
        var installation = new WorkflowInstallationSnapshot(
            $"installation-{suffix}",
            $"publish-{suffix}",
            "scope-alpha",
            "team-alpha",
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new WorkflowDeliveryTriggerIntent(WorkflowDeliveryTriggerKind.OneShot, null, null, true),
            $"source-{suffix}",
            $"resolved-{suffix}",
            yaml,
            [],
            plan,
            owner,
            $"installation-{suffix}:provision:a{attempt}",
            status,
            status == WorkflowInstallationStatus.Accepted ? "accepted" : "provisioning_accepted",
            null,
            null,
            status == WorkflowInstallationStatus.ProvisioningAccepted ? $"wf-{suffix}" : null,
            status == WorkflowInstallationStatus.ProvisioningAccepted ? $"m-{suffix}" : null,
            status == WorkflowInstallationStatus.ProvisioningAccepted ? $"svc-{suffix}" : null,
            status == WorkflowInstallationStatus.ProvisioningAccepted ? $"revision-{suffix}" : null,
            status == WorkflowInstallationStatus.ProvisioningAccepted ? $"bind-{suffix}" : null,
            null,
            status == WorkflowInstallationStatus.ProvisioningAccepted ? $"schedule-provision-{suffix}" : null,
            status == WorkflowInstallationStatus.ProvisioningAccepted ? "pending_binding" : null,
            null,
            attempt,
            now,
            now);
        return new WorkflowDeliverySnapshot(
            $"delivery-{suffix}",
            new WorkflowDeliveryPackageSnapshot(
                $"package-{suffix}",
                $"package-{suffix}@source-{suffix}",
                workflowName,
                "1",
                $"Workflow {suffix}",
                "Description",
                yaml,
                $"source-{suffix}",
                $"package-hash-{suffix}",
                [],
                [],
                [],
                string.Empty,
                [],
                WorkflowDeliveryAcceptancePolicies.Resolve(workflowName),
                "admin-alpha",
                now),
            "scope-alpha",
            now.AddDays(7),
            null,
            WorkflowDeliveryLifecycleStatus.Active,
            "admin-alpha",
            now,
            null,
            null,
            null,
            [],
            installation,
            4,
            now);
    }

    private static WorkflowDeliverySnapshot RetriedDelivery(WorkflowDeliveryTriggerKind triggerKind)
    {
        var delivery = Delivery(WorkflowInstallationStatus.Accepted, attempt: 2);
        var trigger = triggerKind == WorkflowDeliveryTriggerKind.Cron
            ? new WorkflowDeliveryTriggerIntent(triggerKind, "0 9 * * 1-5", "Asia/Singapore", false)
            : new WorkflowDeliveryTriggerIntent(triggerKind, null, "UTC", true);
        return delivery with
        {
            Installation = delivery.Installation! with
            {
                TriggerIntent = trigger,
                WorkflowId = "wf-alpha",
                MemberId = "m-alpha",
                PublishedServiceId = "svc-alpha",
                RevisionId = "revision-alpha",
                BindingRunId = "bind-alpha",
                ScheduleId = "schedule-alpha",
                ScheduleProvisioningId = null,
                ScheduleProvisioningStatus = null,
                ReadinessEvidence = null,
            },
        };
    }

    private sealed class RecordingProvisioningService : IStudioWorkflowProvisioningService
    {
        public Exception? Failure { get; init; }

        public string ResponseScheduleProvisioningId { get; init; } = "schedule-provision-alpha";

        public List<ProvisionWorkflowRequest> Requests { get; } = [];

        public Task<ProvisionWorkflowPreparation> PrepareAsync(
            string scopeId,
            ProvisionWorkflowCallerCredential callerCredential,
            ProvisionWorkflowRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ProvisionWorkflowResponse> ProvisionAsync(
            string scopeId,
            ProvisionWorkflowCallerCredential callerCredential,
            ProvisionWorkflowRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Failure != null)
                return Task.FromException<ProvisionWorkflowResponse>(Failure);
            return Task.FromResult(new ProvisionWorkflowResponse(
                "m-alpha",
                scopeId,
                request.TeamId ?? string.Empty,
                ProvisionWorkflowBindingStatusNames.Accepted,
                "/admin#/observatory")
            {
                WorkflowId = "wf-alpha",
                PublishedServiceId = "svc-alpha",
                RevisionId = "revision-alpha",
                BindingRunId = "bind-alpha",
                ScheduleId = "schedule-alpha",
                ScheduleProvisioningId = ResponseScheduleProvisioningId,
                ScheduleProvisioningStatus = "pending_binding",
            });
        }
    }

    private sealed class RecordingAccessTokenProvider : IWorkflowCallerAccessTokenProvider
    {
        public List<WorkflowCallerNyxIdAuthority> Authorities { get; } = [];

        public Task<string> IssueAsync(
            WorkflowCallerNyxIdAuthority authority,
            CancellationToken ct = default)
        {
            Authorities.Add(authority.Clone());
            return Task.FromResult("token-alpha");
        }
    }

    internal sealed class RecordingCommandPort : IWorkflowDeliveryCommandPort
    {
        public Exception? ProvisioningAcceptedFailure { get; init; }

        public List<RecordWorkflowProvisioningAcceptedMutation> ProvisioningAccepted { get; } = [];

        public List<RecordWorkflowInstallationFailedMutation> Failed { get; } = [];

        public Task<WorkflowDeliveryCommandReceipt> RecordProvisioningAcceptedAsync(
            RecordWorkflowProvisioningAcceptedMutation mutation,
            CancellationToken ct = default)
        {
            ProvisioningAccepted.Add(mutation);
            if (ProvisioningAcceptedFailure != null)
                return Task.FromException<WorkflowDeliveryCommandReceipt>(ProvisioningAcceptedFailure);
            return Accepted(mutation.DeliveryId);
        }

        public Task<WorkflowDeliveryCommandReceipt> RecordInstallationFailedAsync(
            RecordWorkflowInstallationFailedMutation mutation,
            CancellationToken ct = default)
        {
            Failed.Add(mutation);
            return Accepted(mutation.DeliveryId);
        }

        public Task<WorkflowDeliveryCommandReceipt> CreateAsync(CreateWorkflowDeliveryMutation mutation, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliveryCommandReceipt> RecordAccessAsync(RecordWorkflowDeliveryAccessMutation mutation, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliveryCommandReceipt> RevokeAsync(RevokeWorkflowDeliveryMutation mutation, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliveryCommandReceipt> BeginConnectionAsync(BeginWorkflowDeliveryConnectionMutation mutation, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliveryCommandReceipt> UpdateConnectionAsync(UpdateWorkflowDeliveryConnectionMutation mutation, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliveryCommandReceipt> StartInstallationAsync(StartWorkflowInstallationMutation mutation, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliveryCommandReceipt> RetryInstallationAsync(RetryWorkflowInstallationMutation mutation, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliveryCommandReceipt> RecordInstallationReadyAsync(RecordWorkflowInstallationReadyMutation mutation, CancellationToken ct = default) =>
            throw new NotSupportedException();

        private static Task<WorkflowDeliveryCommandReceipt> Accepted(string deliveryId) =>
            Task.FromResult(new WorkflowDeliveryCommandReceipt(
                deliveryId,
                $"actor-{deliveryId}",
                $"command-{deliveryId}",
                $"correlation-{deliveryId}",
                WorkflowDeliveryCommandAckStage.AcceptedForDispatch,
                null));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
