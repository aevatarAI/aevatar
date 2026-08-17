using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioWorkflowScheduleProvisioningExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_WhenWorkflowEvidenceProjectionIsPending_ShouldReturnRetryableResult()
    {
        var port = new RecordingSchedulePort
        {
            PreflightResult = new StudioMemberWorkflowAuthorizationResult(
                false,
                null,
                ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                "workflow_authorization_evidence_not_found: revision rev-2"),
        };
        var sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var result = await sut.ExecuteAsync(NewExecution());

        result.Success.Should().BeFalse();
        result.Retryable.Should().BeTrue();
        result.FailureCode.Should().Be("workflow_authorization_evidence_not_found");
        port.GetScheduleIds.Should().BeEmpty();
        port.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenServingRevisionProjectionIsPending_ShouldReturnRetryableResult()
    {
        var port = new RecordingSchedulePort
        {
            PreflightException = new StudioMemberAutomationPlanConflictException(
                "serving_revision_not_ready",
                "The published member service has no invoke-ready serving revision."),
        };
        var sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var result = await sut.ExecuteAsync(NewExecution());

        result.Success.Should().BeFalse();
        result.Retryable.Should().BeTrue();
        result.FailureCode.Should().Be("serving_revision_not_ready");
        port.GetScheduleIds.Should().BeEmpty();
        port.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenServingTargetChangesDuringPreflight_ShouldFailClosed()
    {
        var port = new RecordingSchedulePort
        {
            PreflightException = new StudioMemberAutomationPlanConflictException(
                "serving_target_changed",
                "The member serving target changed."),
        };
        var sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var action = () => sut.ExecuteAsync(NewExecution());

        var conflict = await action.Should().ThrowAsync<StudioMemberAutomationPlanConflictException>();
        conflict.Which.Code.Should().Be("serving_target_changed");
    }

    [Fact]
    public async Task ExecuteAsync_WhenWorkflowEvidenceIsVisible_ShouldCreateSchedulePinnedToExactRevision()
    {
        var port = new RecordingSchedulePort();
        var sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var result = await sut.ExecuteAsync(NewExecution());

        result.Success.Should().BeTrue();
        result.ScheduleId.Should().Be("provision-svc-1");
        port.CreateRequests.Should().ContainSingle();
        var request = port.CreateRequests[0];
        request.AcceptedBinding.Should().BeEquivalentTo(
            new StudioMemberWorkflowAcceptedBindingContext(
                "team-1", "svc-1", "wf-1", "rev-2"));
        request.ConfirmedPolicyVersion.Should().Be("policy-v1");
        request.ProvisioningBearerToken.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenPublishedServiceIdContainsUnsafeCharacters_ShouldCreateScheduleWithSafeStableId()
    {
        var port = new RecordingSchedulePort();
        var sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var result = await sut.ExecuteAsync(NewExecution(publishedServiceId: "scope-1/service:workflow-alpha"));

        result.Success.Should().BeTrue();
        result.ScheduleId.Should().StartWith("provision-");
        result.ScheduleId.Should().MatchRegex("^provision-[a-f0-9]{32}$");
        port.CreateRequests.Should().ContainSingle();
        port.CreateRequests[0].ScheduleId.Should().Be(result.ScheduleId);
        port.CreateRequests[0].AcceptedBinding!.PublishedServiceId.Should().Be("scope-1/service:workflow-alpha");
    }

    [Fact]
    public async Task ExecuteAsync_WhenUnsafePublishedServiceIdFirstGenerationIsTombstoned_ShouldCreateSecondSafeGeneration()
    {
        const string publishedServiceId = "scope-1/service:workflow-alpha";
        var expectedFirst = $"provision-{HashSuffix(publishedServiceId)}";
        var port = new RecordingSchedulePort();
        port.TombstonedScheduleIds.Add(expectedFirst);
        var sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var result = await sut.ExecuteAsync(NewExecution(publishedServiceId: publishedServiceId));

        result.Success.Should().BeTrue();
        result.ScheduleId.Should().Be($"{expectedFirst}.2");
        port.GetScheduleIds.Should().Equal(expectedFirst, $"{expectedFirst}.2");
        port.CreateRequests.Select(static request => request.ScheduleId).Should()
            .Equal(expectedFirst, $"{expectedFirst}.2");
    }

    [Fact]
    public async Task ExecuteAsync_WhenAuthorizationPlanChangesBeforeWrite_ShouldRetryWithFreshPreflight()
    {
        var port = new RecordingSchedulePort
        {
            CreateException = new StudioMemberAutomationPlanConflictException(
                "authorization_plan_changed",
                "scheduled_invocation_authorization_plan_changed"),
        };
        var sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var retry = await sut.ExecuteAsync(NewExecution());

        retry.Success.Should().BeFalse();
        retry.Retryable.Should().BeTrue();
        retry.FailureCode.Should().Be("authorization_plan_changed");
        retry.Detail.Should().Be("scheduled_invocation_authorization_plan_changed");

        port.PreflightResult = NewAuthorizationResult("permission-digest-2");
        var succeeded = await sut.ExecuteAsync(NewExecution());

        succeeded.Success.Should().BeTrue();
        port.PreflightCallCount.Should().Be(2);
        port.ConfirmedPermissionDigests.Should().Equal(
            "permission-digest-1",
            "permission-digest-2");
        port.CreateRequests.Select(static request => request.OperationId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ExecuteAsync_WhenNonRetryablePlanConflictOccurs_ShouldFailClosed()
    {
        var port = new RecordingSchedulePort
        {
            CreateException = new StudioMemberAutomationPlanConflictException(
                "schedule_target_changed",
                "The stored schedule target changed."),
        };
        var sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var action = () => sut.ExecuteAsync(NewExecution());

        var conflict = await action.Should().ThrowAsync<StudioMemberAutomationPlanConflictException>();
        conflict.Which.Code.Should().Be("schedule_target_changed");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCredentialMaterializationPlanChanges_ShouldFailClosed()
    {
        var port = new RecordingSchedulePort
        {
            CreateException = new StudioMemberAutomationPlanConflictException(
                "authorization_plan_changed",
                "authorization_plan_changed",
                ScheduledAuthorizationPlanMismatchReason.ScopePlanVersionsMismatch),
        };
        var sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var action = () => sut.ExecuteAsync(NewExecution());

        var conflict = await action.Should().ThrowAsync<StudioMemberAutomationPlanConflictException>();
        conflict.Which.AuthorizationPlanMismatchReason.Should().Be(
            ScheduledAuthorizationPlanMismatchReason.ScopePlanVersionsMismatch);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAuthorizationPlanChangesBeforeReplacement_ShouldReturnRetryableResult()
    {
        var port = new RecordingSchedulePort
        {
            Existing = NewExistingAutomation("rev-1"),
            ReplaceException = new StudioMemberAutomationPlanConflictException(
                "authorization_plan_changed",
                "scheduled_invocation_authorization_plan_changed"),
        };
        var sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var result = await sut.ExecuteAsync(NewExecution());

        result.Success.Should().BeFalse();
        result.Retryable.Should().BeTrue();
        result.FailureCode.Should().Be("authorization_plan_changed");
        port.ReplaceRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_WhenExistingScheduleTargetsDifferentService_ShouldFailClosed()
    {
        var port = new RecordingSchedulePort
        {
            Existing = NewExistingAutomation("rev-2", publishedServiceId: "svc-other"),
        };
        var sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var action = () => sut.ExecuteAsync(NewExecution());

        var conflict = await action.Should().ThrowAsync<StudioMemberAutomationPlanConflictException>();
        conflict.Which.Code.Should().Be("schedule_target_changed");
        port.ReplaceRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenExistingScheduleAlreadyMatchesRevisionAndOperation_ShouldReturnExistingSchedule()
    {
        var execution = NewExecution();
        var createPort = new RecordingSchedulePort();
        var sut = new StudioWorkflowScheduleProvisioningExecutor(createPort);
        var created = await sut.ExecuteAsync(execution);
        var expectedOperationId = created.OperationId;
        var port = new RecordingSchedulePort
        {
            Existing = NewExistingAutomation("rev-2", operationId: expectedOperationId),
        };
        sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var result = await sut.ExecuteAsync(execution);

        result.Success.Should().BeTrue();
        result.ScheduleId.Should().Be("provision-svc-1");
        result.OperationId.Should().Be(expectedOperationId);
        port.CreateRequests.Should().BeEmpty();
        port.ReplaceRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenIntentIsOneShot_ShouldCreateOneShotSchedule()
    {
        var fireAt = DateTimeOffset.Parse("2026-08-13T10:30:00+08:00");
        var port = new RecordingSchedulePort();
        var sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var result = await sut.ExecuteAsync(NewExecution(
            scheduleMode: ScheduledDispatchScheduleMode.OneShotAtUtc,
            cronExpression: null,
            timezone: null,
            oneShotFireAt: fireAt));

        result.Success.Should().BeTrue();
        var request = port.CreateRequests.Should().ContainSingle().Subject;
        request.ScheduleMode.Should().Be(ScheduledDispatchScheduleMode.OneShotAtUtc);
        request.ScheduleCron.Should().BeEmpty();
        request.ScheduleTimezone.Should().Be(ScheduledDispatchCalculator.DefaultTimezone);
        request.OneShotFireAt.Should().Be(fireAt.ToUniversalTime());
    }

    [Fact]
    public async Task ExecuteAsync_WhenOneShotFireAtIsMissing_ShouldFailFast()
    {
        var sut = new StudioWorkflowScheduleProvisioningExecutor(new RecordingSchedulePort());

        var action = () => sut.ExecuteAsync(NewExecution(
            scheduleMode: ScheduledDispatchScheduleMode.OneShotAtUtc,
            cronExpression: null,
            timezone: null,
            oneShotFireAt: null));

        var failure = await action.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Be("one_shot_fire_at_required");
    }

    [Fact]
    public async Task ExecuteAsync_WhenLiveSchedulePinsOlderRevision_ShouldReplaceAndPinNewRevision()
    {
        var port = new RecordingSchedulePort
        {
            Existing = NewExistingAutomation("rev-1"),
        };
        var sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var result = await sut.ExecuteAsync(NewExecution());

        result.Success.Should().BeTrue();
        result.ScheduleId.Should().Be("provision-svc-1");
        port.CreateRequests.Should().BeEmpty();
        port.ReplaceRequests.Should().ContainSingle();
        var request = port.ReplaceRequests[0];
        request.ScheduleId.Should().Be("provision-svc-1");
        request.OperationId.Should().StartWith("studio-workflow-provision-replace:");
        request.AcceptedBinding!.WorkflowRevisionId.Should().Be("rev-2");
    }

    [Fact]
    public async Task ExecuteAsync_WhenFirstGenerationIsTombstoned_ShouldCreateSecondGeneration()
    {
        var port = new RecordingSchedulePort();
        port.TombstonedScheduleIds.Add("provision-svc-1");
        var sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var result = await sut.ExecuteAsync(NewExecution());

        result.Success.Should().BeTrue();
        result.ScheduleId.Should().Be("provision-svc-1.2");
        port.GetScheduleIds.Should().Equal("provision-svc-1", "provision-svc-1.2");
        port.CreateRequests.Select(static request => request.ScheduleId).Should()
            .Equal("provision-svc-1", "provision-svc-1.2");
    }

    [Fact]
    public async Task ExecuteAsync_WhenExplicitOperationIdentityIsProvided_ShouldUseItVerbatim()
    {
        var port = new RecordingSchedulePort();
        var sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var result = await sut.ExecuteAsync(NewExecution(
            scheduleOperationId: "custom-operation-id",
            scheduleIdempotencyKey: "custom-idempotency-key"));

        result.Success.Should().BeTrue();
        port.CreateRequests.Should().ContainSingle();
        port.CreateRequests[0].OperationId.Should().Be("custom-operation-id");
        port.CreateRequests[0].IdempotencyKey.Should().Be("custom-idempotency-key");
        port.CreateRequests[0].OperationId.Should().NotStartWith("studio-workflow-provision-create:");
    }

    [Fact]
    public async Task ExecuteAsync_WhenAllScheduleGenerationsAreTombstoned_ShouldFailAfterExhaustion()
    {
        var port = new RecordingSchedulePort();
        for (var generation = 1; generation <= 50; generation++)
        {
            port.TombstonedScheduleIds.Add(generation == 1 ? "provision-svc-1" : $"provision-svc-1.{generation}");
        }

        var sut = new StudioWorkflowScheduleProvisioningExecutor(port);

        var result = await sut.ExecuteAsync(NewExecution());

        result.Success.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.FailureCode.Should().Be("schedule_generations_exhausted");
        port.CreateRequests.Should().HaveCount(50);
        port.GetScheduleIds.Should().HaveCount(50);
    }

    private static string HashSuffix(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    private static StudioWorkflowScheduleProvisioningExecution NewExecution(
        string publishedServiceId = "svc-1",
        ScheduledDispatchScheduleMode scheduleMode = ScheduledDispatchScheduleMode.RecurringCron,
        string? cronExpression = "0 9 * * *",
        string? timezone = "UTC",
        DateTimeOffset? oneShotFireAt = null,
        string? scheduleOperationId = null,
        string? scheduleIdempotencyKey = null) =>
        new(
            new StudioWorkflowScheduleProvisioningIntent(
                "schedule-provisioning-1",
                "scope-1",
                "team-1",
                "m-1",
                publishedServiceId,
                "wf-1",
                "rev-2",
                "Monitor",
                "run",
                new AuthenticatedAuthorizationOwnerContext(
                    new AuthorizationOwnerIdentity
                    {
                        Authority = NyxIdAuthorizationAuthorities.NyxId,
                        OwnerKind = AuthorizationOwnerKind.Personal,
                        OwnerSubject = "owner-1",
                    },
                    "nyxid",
                    string.Empty,
                    "owner-1",
                    "binding-1"),
                scheduleMode,
                cronExpression,
                timezone,
                30,
                "bind-1")
            {
                ScheduleOperationId = scheduleOperationId,
                ScheduleIdempotencyKey = scheduleIdempotencyKey,
            },
            oneShotFireAt);

    private static StudioMemberAutomationView NewExistingAutomation(
        string revisionId,
        string publishedServiceId = "svc-1",
        string operationId = "studio-workflow-provision-create:old") =>
        new(
            "scope-1",
            "team-1",
            "m-1",
            "provision-svc-1",
            publishedServiceId,
            "Monitor",
            "old run",
            "0 8 * * *",
            "UTC",
            true,
            "active",
            DateTimeOffset.UtcNow.AddDays(1),
            string.Empty,
            operationId,
            1,
            false,
            DateTimeOffset.UtcNow.AddHours(1),
            null,
            10)
        {
            TargetRevisionId = revisionId,
        };

    private static StudioMemberWorkflowAuthorizationResult NewAuthorizationResult(
        string permissionDigest) =>
        new(
            true,
            new ScheduledInvocationAuthorizationPlan
            {
                PermissionDigest = permissionDigest,
                CredentialPolicy = new ScheduledInvocationCredentialPolicy
                {
                    PolicyVersion = "policy-v1",
                },
            },
            ScheduledInvocationAuthorizationFailureCode.Unspecified,
            string.Empty);

    private sealed class RecordingSchedulePort : IStudioMemberWorkflowSchedulePort
    {
        public StudioMemberWorkflowAuthorizationResult PreflightResult { get; set; } =
            NewAuthorizationResult("permission-digest-1");

        public Exception? PreflightException { get; init; }

        public int PreflightCallCount { get; private set; }

        public StudioMemberAutomationView? Existing { get; init; }

        public Exception? CreateException { get; set; }

        public Exception? ReplaceException { get; set; }

        public List<string> GetScheduleIds { get; } = [];

        public List<StudioMemberWorkflowScheduleRequest> CreateRequests { get; } = [];

        public List<StudioMemberWorkflowScheduleRequest> ReplaceRequests { get; } = [];

        public List<string> ConfirmedPermissionDigests { get; } = [];

        public HashSet<string> TombstonedScheduleIds { get; } = new(StringComparer.Ordinal);

        public Task<StudioMemberWorkflowAuthorizationResult> PreflightAsync(
            StudioMemberWorkflowScheduleRequest request,
            CancellationToken ct = default) =>
            PreflightForWriteAsync(request, ct);

        public Task<StudioMemberWorkflowAuthorizationResult> PreflightForWriteAsync(
            StudioMemberWorkflowScheduleRequest request,
            CancellationToken ct = default)
        {
            PreflightCallCount++;
            if (PreflightException is { } exception)
                throw exception;

            return Task.FromResult(PreflightResult);
        }

        public Task<StudioMemberAutomationView?> GetAsync(
            string scopeId,
            string teamId,
            string memberId,
            string scheduleId,
            CancellationToken ct = default)
        {
            GetScheduleIds.Add(scheduleId);
            return Task.FromResult(
                Existing != null && string.Equals(Existing.ScheduleId, scheduleId, StringComparison.Ordinal)
                    ? Existing
                    : null);
        }

        public Task<StudioMemberWorkflowScheduleResult> CreateAsync(
            StudioMemberWorkflowScheduleRequest request,
            string confirmedPermissionDigest,
            CancellationToken ct = default)
        {
            CreateRequests.Add(request);
            ConfirmedPermissionDigests.Add(confirmedPermissionDigest);
            if (CreateException is { } exception)
            {
                CreateException = null;
                throw exception;
            }

            var scheduleId = request.ScheduleId!;
            if (TombstonedScheduleIds.Contains(scheduleId))
                throw new ScheduledDispatchNotFoundException(scheduleId);

            return Task.FromResult(NewScheduleResult(request));
        }

        public Task<StudioMemberWorkflowScheduleResult> ReplaceAsync(
            StudioMemberWorkflowScheduleRequest request,
            string confirmedPermissionDigest,
            CancellationToken ct = default)
        {
            ReplaceRequests.Add(request);
            if (ReplaceException is { } exception)
            {
                ReplaceException = null;
                throw exception;
            }

            return Task.FromResult(NewScheduleResult(request));
        }

        public Task<StudioMemberWorkflowScheduleResult> ReauthorizeAsync(
            StudioMemberWorkflowScheduleRequest request,
            string confirmedPermissionDigest,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationListResponse> ListAsync(
            string scopeId,
            string teamId,
            string? memberId,
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> UpdateAsync(
            StudioMemberAutomationUpdateCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> PauseAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> ResumeAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> RunNowAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> DeleteAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        private static StudioMemberWorkflowScheduleResult NewScheduleResult(
            StudioMemberWorkflowScheduleRequest request) =>
            new(
                true,
                request.ScopeId,
                request.MemberId,
                request.ScheduleId!,
                request.AcceptedBinding!.PublishedServiceId,
                "/admin#/observatory",
                "pending")
            {
                OperationId = request.OperationId ?? string.Empty,
            };
    }
}
