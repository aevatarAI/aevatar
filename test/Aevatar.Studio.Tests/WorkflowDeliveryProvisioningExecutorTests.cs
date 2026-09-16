using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Delivery;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using ApplicationAcceptanceDateProjection = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryAcceptanceDateProjection;
using ProtobufValue = Google.Protobuf.WellKnownTypes.Value;

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

        var result = await executor.ExecuteAsync(delivery, "worker-alpha");

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
        request.Prompt.Should().BeNull();
        request.AcceptanceInput.Should().BeEquivalentTo(delivery.Installation!.AcceptanceInput);
        commands.ProvisioningAccepted.Should().ContainSingle();
        commands.ProvisioningAccepted[0].Attempt.Should().Be(2);
        commands.ProvisioningAccepted[0].OperationId.Should().Be("installation-alpha:provision:a2");
        commands.ProvisioningAccepted[0].ContinuationClaimId.Should().Be("claim-alpha");
        commands.ProvisioningAccepted[0].ContinuationClaimantId.Should().Be("worker-alpha");
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

        var result = await executor.ExecuteAsync(delivery, "worker-alpha");

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

        var result = await executor.ExecuteAsync(delivery, "worker-alpha");

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.Failed);
        result.ErrorCode.Should().Be("PROVISIONING_FAILED");
        commands.ProvisioningAccepted.Should().BeEmpty();
        commands.Failed.Should().ContainSingle();
        var failed = commands.Failed[0];
        failed.Attempt.Should().Be(3);
        failed.OperationId.Should().Be("installation-alpha:provision:a3");
        failed.ExpectedStatus.Should().Be(WorkflowInstallationStatus.Accepted);
        failed.ContinuationClaimId.Should().Be("claim-alpha");
        failed.ContinuationClaimantId.Should().Be("worker-alpha");
        failed.ErrorMessage.Should().Be("Workflow provisioning failed.");
        failed.ErrorMessage.Should().NotContain("token-alpha");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCatalogProjectionIsPending_ShouldLeaveAcceptedInstallationForRetry()
    {
        var provisioning = new RecordingProvisioningService
        {
            Failure = new StudioMemberAutomationProjectionPendingException(31),
        };
        var commands = new RecordingCommandPort();
        var executor = NewExecutor(provisioning, commands, new RecordingAccessTokenProvider());

        var execute = () => executor.ExecuteAsync(
            Delivery(WorkflowInstallationStatus.Accepted),
            "worker-alpha");

        var pending = await execute.Should()
            .ThrowAsync<StudioMemberAutomationProjectionPendingException>();
        pending.Which.RequiredStateVersion.Should().Be(31);
        commands.ProvisioningAccepted.Should().BeEmpty();
        commands.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenAdmissionFails_ShouldPersistSafeBlockerEvidence()
    {
        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            Status = ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable,
            Blockers =
            {
                new ExternalCapabilityBlocker
                {
                    Status = ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable,
                    Code = "DURABLE_AUTHORIZATION_UNAVAILABLE",
                    SafeMessage = "The durable authorization catalog is unavailable.",
                },
            },
        };
        var provisioning = new RecordingProvisioningService
        {
            Failure = new WorkflowExternalCapabilityAdmissionException(readiness),
        };
        var commands = new RecordingCommandPort();
        var executor = NewExecutor(provisioning, commands, new RecordingAccessTokenProvider());

        var result = await executor.ExecuteAsync(
            Delivery(WorkflowInstallationStatus.Accepted),
            "worker-alpha");

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.Failed);
        result.ErrorCode.Should().Be("DURABLE_AUTHORIZATION_UNAVAILABLE");
        var failed = commands.Failed.Should().ContainSingle().Which;
        failed.ErrorCode.Should().Be("DURABLE_AUTHORIZATION_UNAVAILABLE");
        failed.ErrorMessage.Should().Be("The durable authorization catalog is unavailable.");
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

        var execute = () => executor.ExecuteAsync(delivery, "worker-alpha");

        await execute.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*ACK was not observed*");
        provisioning.Requests.Should().ContainSingle();
        commands.ProvisioningAccepted.Should().ContainSingle();
        commands.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WithoutCommittedContinuationClaim_ShouldSkipBeforeMintingOrProvisioning()
    {
        var provisioning = new RecordingProvisioningService();
        var commands = new RecordingCommandPort();
        var tokens = new RecordingAccessTokenProvider();
        var executor = NewExecutor(provisioning, commands, tokens);
        var delivery = Delivery(
            WorkflowInstallationStatus.Accepted,
            includeContinuationClaim: false);

        var result = await executor.ExecuteAsync(delivery, "worker-alpha");

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.Skipped);
        tokens.Authorities.Should().BeEmpty();
        provisioning.Requests.Should().BeEmpty();
        commands.ProvisioningAccepted.Should().BeEmpty();
        commands.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenClaimBelongsToAnotherWorker_ShouldSkipBeforeSideEffects()
    {
        var provisioning = new RecordingProvisioningService();
        var commands = new RecordingCommandPort();
        var tokens = new RecordingAccessTokenProvider();
        var executor = NewExecutor(provisioning, commands, tokens);
        var delivery = Delivery(
            WorkflowInstallationStatus.Accepted,
            claimantId: "worker-beta");

        var result = await executor.ExecuteAsync(delivery, "worker-alpha");

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.Skipped);
        tokens.Authorities.Should().BeEmpty();
        provisioning.Requests.Should().BeEmpty();
        commands.ProvisioningAccepted.Should().BeEmpty();
        commands.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenOwnedClaimPrecededRevocation_ShouldFinishWithinActorLease()
    {
        var provisioning = new RecordingProvisioningService();
        var commands = new RecordingCommandPort();
        var tokens = new RecordingAccessTokenProvider();
        var executor = NewExecutor(provisioning, commands, tokens);
        var delivery = Delivery(
            WorkflowInstallationStatus.Accepted,
            claimAtUtc: DateTimeOffset.Parse("2026-08-16T04:01:00Z"),
            claimExpiresAtUtc: DateTimeOffset.Parse("2026-08-16T04:04:00Z")) with
        {
            LifecycleStatus = WorkflowDeliveryLifecycleStatus.Revoked,
            RevokedBy = "admin-alpha",
            RevokedAtUtc = DateTimeOffset.Parse("2026-08-16T04:00:00Z"),
        };

        var result = await executor.ExecuteAsync(delivery, "worker-alpha");

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.ProvisioningAccepted);
        tokens.Authorities.Should().ContainSingle();
        provisioning.Requests.Should().ContainSingle();
        commands.ProvisioningAccepted.Should().ContainSingle();
        commands.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenPackageSupportsAutomaticAcceptance_ShouldForwardTypedPackageInput()
    {
        var provisioning = new RecordingProvisioningService();
        var executor = NewExecutor(
            provisioning,
            new RecordingCommandPort(),
            new RecordingAccessTokenProvider());
        var delivery = Delivery(WorkflowInstallationStatus.Accepted);

        var result = await executor.ExecuteAsync(delivery, "worker-alpha");

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.ProvisioningAccepted);
        var request = provisioning.Requests.Should().ContainSingle().Which;
        request.Prompt.Should().BeNull();
        request.AcceptanceInput!.Fields["dry_run"].BoolValue.Should().BeTrue();
        request.AcceptanceInput.Fields["limit"].NumberValue.Should().Be(5);
    }

    [Fact]
    public async Task ExecuteAsync_WhenLegacyPackageHasNoAcceptanceInput_ShouldRequireMigration()
    {
        var provisioning = new RecordingProvisioningService();
        var commands = new RecordingCommandPort();
        var tokens = new RecordingAccessTokenProvider();
        var executor = NewExecutor(
            provisioning,
            commands,
            tokens);
        var delivery = Delivery(WorkflowInstallationStatus.Accepted);
        delivery = delivery with
        {
            Package = delivery.Package with
            {
                AcceptancePolicy = delivery.Package.AcceptancePolicy with
                {
                    Input = new WorkflowDeliveryAcceptanceInputRecipe(new Struct(), []),
                    InputDeclared = false,
                },
            },
        };

        var result = await executor.ExecuteAsync(delivery, "worker-alpha");

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.Failed);
        result.ErrorCode.Should().Be("DELIVERY_ACCEPTANCE_INPUT_MIGRATION_REQUIRED");
        provisioning.Requests.Should().BeEmpty();
        tokens.Authorities.Should().BeEmpty();
        var failed = commands.Failed.Should().ContainSingle().Which;
        failed.ErrorCode.Should().Be("DELIVERY_ACCEPTANCE_INPUT_MIGRATION_REQUIRED");
        failed.ErrorMessage.Should().Contain("revoked");
        failed.ErrorMessage.Should().Contain("recreated");
        failed.ErrorMessage.Should().Contain("reinstall");
    }

    [Fact]
    public async Task ExecuteAsync_WhenInstallationHasNoCommittedAcceptanceInput_ShouldRequireMigration()
    {
        var provisioning = new RecordingProvisioningService();
        var commands = new RecordingCommandPort();
        var tokens = new RecordingAccessTokenProvider();
        var executor = NewExecutor(provisioning, commands, tokens);
        var delivery = Delivery(WorkflowInstallationStatus.Accepted);
        delivery = delivery with
        {
            Installation = delivery.Installation! with { AcceptanceInput = null },
        };

        var result = await executor.ExecuteAsync(delivery, "worker-alpha");

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.Failed);
        result.ErrorCode.Should().Be("DELIVERY_ACCEPTANCE_INPUT_MIGRATION_REQUIRED");
        provisioning.Requests.Should().BeEmpty();
        tokens.Authorities.Should().BeEmpty();
        var failed = commands.Failed.Should().ContainSingle().Which;
        failed.ErrorCode.Should().Be("DELIVERY_ACCEPTANCE_INPUT_MIGRATION_REQUIRED");
        failed.ErrorMessage.Should().Contain("revoked");
        failed.ErrorMessage.Should().Contain("recreated");
        failed.ErrorMessage.Should().Contain("reinstall");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldForwardCommittedInputWithoutReevaluatingPackageRecipe()
    {
        var provisioning = new RecordingProvisioningService();
        var executor = NewExecutor(
            provisioning,
            new RecordingCommandPort(),
            new RecordingAccessTokenProvider());
        var committed = new Struct
        {
            Fields =
            {
                ["reference"] = ProtobufValue.ForString("run-20261231"),
            },
        };
        var delivery = Delivery(
            WorkflowInstallationStatus.Accepted,
            acceptanceInput: committed);
        delivery = delivery with
        {
            Package = delivery.Package with
            {
                AcceptancePolicy = delivery.Package.AcceptancePolicy with
                {
                    Input = new WorkflowDeliveryAcceptanceInputRecipe(
                        new Struct(),
                        [
                            new WorkflowDeliveryAcceptanceInputBinding(
                                "reference",
                                "changed-",
                                string.Empty,
                                new WorkflowDeliveryInstallationCreatedAtUtcInput(
                                    ApplicationAcceptanceDateProjection.UtcDate,
                                    1)),
                        ]),
                },
            },
        };

        var result = await executor.ExecuteAsync(delivery, "worker-alpha");

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.ProvisioningAccepted);
        var request = provisioning.Requests.Should().ContainSingle().Which;
        request.AcceptanceInput.Should().BeEquivalentTo(committed);
        request.AcceptanceInput.Should().NotBeSameAs(delivery.Installation!.AcceptanceInput);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRecipeWasExplicitlyEmpty_ShouldForwardEmptyInput()
    {
        var provisioning = new RecordingProvisioningService();
        var executor = NewExecutor(
            provisioning,
            new RecordingCommandPort(),
            new RecordingAccessTokenProvider());
        var delivery = Delivery(
            WorkflowInstallationStatus.Accepted,
            acceptanceInput: new Struct());

        var result = await executor.ExecuteAsync(delivery, "worker-alpha");

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.ProvisioningAccepted);
        provisioning.Requests.Should().ContainSingle().Which.AcceptanceInput!.Fields.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenManualPackageIsPublishedWithoutAutomaticRun_ShouldForwardTypedInput()
    {
        var provisioning = new RecordingProvisioningService();
        var executor = NewExecutor(
            provisioning,
            new RecordingCommandPort(),
            new RecordingAccessTokenProvider());
        var delivery = Delivery(
            WorkflowInstallationStatus.Accepted,
            acceptanceMode: WorkflowDeliveryAcceptanceMode.Manual,
            acceptanceLimitation: "An external acceptance run is required.");
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

        var result = await executor.ExecuteAsync(delivery, "worker-alpha");

        result.Status.Should().Be(WorkflowDeliveryProvisioningExecutionStatus.ProvisioningAccepted);
        var request = provisioning.Requests.Should().ContainSingle().Which;
        request.RunImmediately.Should().BeFalse();
        request.Cron.Should().BeNull();
        request.Prompt.Should().BeNull();
        request.AcceptanceInput.Should().BeEquivalentTo(delivery.Installation!.AcceptanceInput);
    }

    [Theory]
    [InlineData(WorkflowDeliveryAcceptanceMode.Manual, "AUTOMATIC_ACCEPTANCE_UNSUPPORTED")]
    [InlineData(WorkflowDeliveryAcceptanceMode.Unspecified, "UNSUPPORTED_DELIVERY_PACKAGE")]
    public async Task ExecuteAsync_WhenPolicyCannotRunAutomaticAcceptance_ShouldFailClosed(
        WorkflowDeliveryAcceptanceMode acceptanceMode,
        string expectedCode)
    {
        var provisioning = new RecordingProvisioningService();
        var commands = new RecordingCommandPort();
        var tokens = new RecordingAccessTokenProvider();
        var executor = NewExecutor(provisioning, commands, tokens);
        var delivery = Delivery(
            WorkflowInstallationStatus.Accepted,
            acceptanceMode: acceptanceMode,
            acceptanceLimitation: acceptanceMode == WorkflowDeliveryAcceptanceMode.Manual
                ? "An external acceptance run is required."
                : null);

        var result = await executor.ExecuteAsync(delivery, "worker-alpha");

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
        string workflowName = "workflow-alpha",
        WorkflowDeliveryAcceptanceMode acceptanceMode = WorkflowDeliveryAcceptanceMode.AutomaticPreview,
        string? acceptanceLimitation = null,
        Struct? acceptanceInput = null,
        bool includeContinuationClaim = true,
        string claimantId = "worker-alpha",
        DateTimeOffset? claimAtUtc = null,
        DateTimeOffset? claimExpiresAtUtc = null)
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
        var operationId = $"installation-{suffix}:provision:a{attempt}";
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
            acceptanceInput?.Clone() ?? DefaultAcceptanceInput(),
            operationId,
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
            now)
        {
            ContinuationClaim = includeContinuationClaim
                ? new WorkflowInstallationContinuationClaimSnapshot(
                    $"claim-{suffix}",
                    claimantId,
                    status,
                    attempt,
                    operationId,
                    claimAtUtc ?? now.AddMinutes(59),
                    claimExpiresAtUtc ?? now.AddMinutes(64))
                : null,
        };
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
                new WorkflowDeliveryAcceptancePolicy(
                    acceptanceMode,
                    acceptanceLimitation,
                    new WorkflowDeliveryAcceptanceInputRecipe(
                        acceptanceInput?.Clone() ?? DefaultAcceptanceInput(),
                        [])),
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

    private static Struct DefaultAcceptanceInput() =>
        new()
        {
            Fields =
            {
                ["dry_run"] = ProtobufValue.ForBool(true),
                ["limit"] = ProtobufValue.ForNumber(5),
            },
        };

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
        public Task<WorkflowDeliveryCommandReceipt> AttachConnectionAsync(AttachWorkflowDeliveryConnectionMutation mutation, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliveryCommandReceipt> StartInstallationAsync(StartWorkflowInstallationMutation mutation, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliveryCommandReceipt> RetryInstallationAsync(RetryWorkflowInstallationMutation mutation, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliveryCommandReceipt> ClaimInstallationContinuationAsync(ClaimWorkflowInstallationContinuationMutation mutation, CancellationToken ct = default) =>
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
