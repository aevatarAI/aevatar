using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Delivery;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowInstallationReadinessReconcilerTests
{
    private const string ScopeId = "scope-alpha";
    private const string TeamId = "team-alpha";
    private const string MemberId = "m-alpha";
    private const string ServiceId = "svc-alpha";
    private const string RevisionId = "rev-alpha";
    private const string InstallationId = "installation-alpha";
    private const string ScheduleId = "schedule-alpha";
    private const string WorkflowName = "workflow-alpha";
    private const string ContinuationClaimantId = "worker-alpha";
    private const string AcceptanceOutput =
        "{\"workflow\":\"workflow-alpha\",\"mode\":\"preview\",\"side_effects\":false}";

    private static string ArtifactId => AcceptanceIdentity().ArtifactId;
    private static string ArtifactRevisionId => AcceptanceIdentity().RevisionId;
    private static string Digest => AcceptanceOutputValidation().ContentHash!;

    [Fact]
    public async Task ReconcileAsync_WithoutActorOwnedContinuationClaim_ShouldNotReadOrMutateDownstreamState()
    {
        var context = new TestContext();
        var delivery = Delivery();
        delivery = delivery with
        {
            Installation = delivery.Installation! with { ContinuationClaim = null },
        };

        var result = await context.Reconciler.ReconcileAsync(delivery, ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.Pending);
        result.Code.Should().Be("continuation_claim_pending");
        context.Commands.Ready.Should().BeEmpty();
        context.Member.EndpointContractQueries.Should().BeEmpty();
        context.Runs.Queries.Should().BeEmpty();
        context.Artifacts.Gets.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenClaimBelongsToAnotherWorker_ShouldNotReadOrMutateDownstreamState()
    {
        var context = new TestContext();

        var result = await context.Reconciler.ReconcileAsync(Delivery(), "worker-beta");

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.Pending);
        result.Code.Should().Be("continuation_claim_pending");
        context.Member.EndpointContractQueries.Should().BeEmpty();
        context.Automations.GetQueries.Should().BeEmpty();
        context.Automations.ListQueries.Should().BeEmpty();
        context.Runs.Queries.Should().BeEmpty();
        context.Artifacts.Gets.Should().BeEmpty();
        context.Commands.ProvisioningAccepted.Should().BeEmpty();
        context.Commands.Ready.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenOwnedClaimPrecededRevocationAndClockRollback_ShouldFinishWithinLease()
    {
        var context = new TestContext();
        context.Runs.Items = [SuccessfulRun()];
        var delivery = AcceptanceDelivery();
        delivery = delivery with
        {
            LifecycleStatus = WorkflowDeliveryLifecycleStatus.Revoked,
            RevokedBy = "admin-alpha",
            RevokedAtUtc = DateTimeOffset.Parse("2026-08-16T01:10:00Z"),
            Installation = delivery.Installation! with
            {
                ContinuationClaim = delivery.Installation.ContinuationClaim! with
                {
                    ClaimedAtUtc = DateTimeOffset.Parse("2026-08-16T01:11:00Z"),
                },
            },
        };

        var result = await context.Reconciler.ReconcileAsync(
            delivery,
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.Ready);
        context.Commands.Ready.Should().ContainSingle();
    }

    [Fact]
    public async Task ReconcileAsync_WhenPublishedServiceIdentityDiffers_ShouldReturnTerminalFailure()
    {
        var context = new TestContext();
        context.Member.Contract = EndpointContract(publishedServiceId: "svc-other");

        var result = await context.Reconciler.ReconcileAsync(Delivery(), ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.TerminalFailure);
        result.Code.Should().Be("published_service_identity_mismatch");
        context.Commands.Ready.Should().BeEmpty();
        context.Runs.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenExactRevisionIsNotServing_ShouldRemainPending()
    {
        var context = new TestContext();
        context.Member.Contract = EndpointContract(revisionId: "rev-other");

        var result = await context.Reconciler.ReconcileAsync(Delivery(), ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.Pending);
        result.Code.Should().Be("bound_revision_pending");
        context.Commands.Ready.Should().BeEmpty();
        context.Runs.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenScheduleTargetsDifferentRevision_ShouldReturnTerminalFailure()
    {
        var context = new TestContext();
        context.Automations.GetResult = Automation(targetRevisionId: "rev-other");

        var result = await context.Reconciler.ReconcileAsync(
            Delivery(WorkflowDeliveryTriggerKind.OneShot, scheduleId: ScheduleId),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.TerminalFailure);
        result.Code.Should().Be("schedule_target_mismatch");
        context.Commands.Ready.Should().BeEmpty();
        context.Runs.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenAcceptanceRunIsNonterminal_ShouldRemainPending()
    {
        var context = new TestContext();
        context.Runs.Items = [Run(ServiceRunStatus.Accepted)];

        var result = await context.Reconciler.ReconcileAsync(
            AcceptanceDelivery(),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.Pending);
        result.Code.Should().Be("acceptance_artifact_pending");
        context.Commands.Ready.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenWorkflowCurrentStateCompletedWhileRegistryIsAccepted_ShouldBecomeReady()
    {
        var context = new TestContext();
        var run = SuccessfulRun() with
        {
            Status = ServiceRunStatus.Accepted,
            LastOutput = string.Empty,
        };
        context.Runs.Items = [run];
        context.WorkflowStates.Snapshots[run.TargetActorId] =
            WorkflowCurrentStateQueryPortStub.FromServiceRun(
                run,
                WorkflowRunCompletionStatus.Completed,
                lastSuccess: true,
                lastOutput: AcceptanceOutput,
                stateVersion: 53);

        var result = await context.Reconciler.ReconcileAsync(
            AcceptanceDelivery(),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.Ready);
        result.Evidence!.AcceptanceRun.AcceptanceRunId.Should().Be(run.RunId);
        result.Evidence.AcceptanceRun.CommittedStateVersion.Should().Be(53);
        context.Commands.Ready.Should().ContainSingle();
    }

    [Theory]
    [InlineData(WorkflowRunCompletionStatus.Failed, "acceptance_run_failed")]
    [InlineData(WorkflowRunCompletionStatus.Stopped, "acceptance_run_stopped")]
    [InlineData(WorkflowRunCompletionStatus.TimedOut, "acceptance_run_timed_out")]
    public async Task ReconcileAsync_WhenWorkflowCurrentStateIsTerminalFailureWhileRegistryIsAccepted_ShouldFail(
        WorkflowRunCompletionStatus completionStatus,
        string expectedCode)
    {
        var context = new TestContext();
        var run = Run(ServiceRunStatus.Accepted);
        context.Runs.Items = [run];
        context.WorkflowStates.Snapshots[run.TargetActorId] =
            WorkflowCurrentStateQueryPortStub.FromServiceRun(
                run,
                completionStatus,
                lastSuccess: false,
                lastError: "workflow terminal failure",
                stateVersion: 53);

        var result = await context.Reconciler.ReconcileAsync(
            AcceptanceDelivery(),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.TerminalFailure);
        result.Code.Should().Be(expectedCode);
        result.Message.Should().Be("workflow terminal failure");
        context.Commands.Ready.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenWorkflowCurrentStateIdentityDiffers_ShouldFailClosed()
    {
        var context = new TestContext();
        var run = Run(ServiceRunStatus.Accepted);
        context.Runs.Items = [run];
        var snapshot = WorkflowCurrentStateQueryPortStub.FromServiceRun(
            run,
            WorkflowRunCompletionStatus.Completed,
            lastSuccess: true,
            lastOutput: AcceptanceOutput,
            stateVersion: 53);
        snapshot.RunId = "run-other";
        context.WorkflowStates.Snapshots[run.TargetActorId] = snapshot;

        var result = await context.Reconciler.ReconcileAsync(
            AcceptanceDelivery(),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.TerminalFailure);
        result.Code.Should().Be("acceptance_run_outcome_uncertain");
        result.Message.Should().Contain("identity does not match");
        context.Commands.Ready.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ServiceRunStatus.Failed, "acceptance_run_failed")]
    [InlineData(ServiceRunStatus.Stopped, "acceptance_run_stopped")]
    [InlineData(ServiceRunStatus.OutcomeUncertain, "acceptance_run_outcome_uncertain")]
    public async Task ReconcileAsync_WhenAcceptanceRunFails_ShouldReturnTypedTerminalFailure(
        ServiceRunStatus status,
        string expectedCode)
    {
        var context = new TestContext();
        context.Runs.Items = [Run(status)];

        var result = await context.Reconciler.ReconcileAsync(
            AcceptanceDelivery(),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.TerminalFailure);
        result.Code.Should().Be(expectedCode);
        context.Commands.Ready.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenCompletedRunHasNoVerifiedArtifact_ShouldRemainPending()
    {
        var context = new TestContext();
        context.Runs.Items =
        [
            Run(ServiceRunStatus.Completed) with
            {
                ResultArtifacts =
                [
                    new ContentArtifactReference
                    {
                        ArtifactId = "artifact-alpha",
                        RevisionId = "artifact-rev-alpha",
                        ContentHash = "not-a-sha256-digest",
                        MediaType = "application/json",
                    },
                ],
            },
        ];

        var result = await context.Reconciler.ReconcileAsync(
            AcceptanceDelivery(),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.Pending);
        result.Code.Should().Be("acceptance_artifact_pending");
        context.Commands.Ready.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenArtifactCurrentStateIsNotVisible_ShouldRemainPending()
    {
        var context = new TestContext();
        context.Runs.Items = [SuccessfulRun()];
        context.Artifacts.Current = null;

        var result = await context.Reconciler.ReconcileAsync(
            AcceptanceDelivery(),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.Pending);
        result.Code.Should().Be("acceptance_artifact_projection_pending");
        context.Commands.Ready.Should().BeEmpty();
        context.Artifacts.Gets.Should().ContainSingle().Which.Should().Be((ScopeId, ArtifactId));
    }

    [Fact]
    public async Task ReconcileAsync_WhenArtifactIsTombstoned_ShouldReturnTerminalFailure()
    {
        var context = new TestContext();
        context.Runs.Items = [SuccessfulRun()];
        context.Artifacts.Current = ArtifactCurrent() with
        {
            LifecycleStatus = ContentArtifactLifecycleStatusNames.Tombstoned,
        };

        var result = await context.Reconciler.ReconcileAsync(
            AcceptanceDelivery(),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.TerminalFailure);
        result.Code.Should().Be("acceptance_artifact_tombstoned");
        context.Commands.Ready.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenArtifactRevisionIsNotVisible_ShouldRemainPending()
    {
        var context = new TestContext();
        context.Runs.Items = [SuccessfulRun()];
        context.Artifacts.Current = ArtifactCurrent() with { Revisions = [] };

        var result = await context.Reconciler.ReconcileAsync(
            AcceptanceDelivery(),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.Pending);
        result.Code.Should().Be("acceptance_artifact_revision_pending");
        context.Commands.Ready.Should().BeEmpty();
    }

    [Theory]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "application/json")]
    [InlineData("3b399a596edecc5789dfd6fe810311237498d76591bf65cc59f15bf3bff43710", "text/plain")]
    public async Task ReconcileAsync_WhenArtifactRevisionDoesNotMatchRunReference_ShouldReturnTerminalFailure(
        string contentHash,
        string mediaType)
    {
        var context = new TestContext();
        context.Runs.Items = [SuccessfulRun()];
        context.Artifacts.Current = ArtifactCurrent() with
        {
            Revisions = [ArtifactRevision(contentHash, mediaType)],
        };

        var result = await context.Reconciler.ReconcileAsync(
            AcceptanceDelivery(),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.TerminalFailure);
        result.Code.Should().Be("acceptance_artifact_integrity_mismatch");
        context.Commands.Ready.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ContentArtifactRevisionAvailabilityNames.Redacted)]
    [InlineData(ContentArtifactRevisionAvailabilityNames.RetentionExpired)]
    public async Task ReconcileAsync_WhenArtifactRevisionIsUnavailable_ShouldReturnTerminalFailure(
        string availability)
    {
        var context = new TestContext();
        context.Runs.Items = [SuccessfulRun()];
        context.Artifacts.Current = ArtifactCurrent() with
        {
            Revisions = [ArtifactRevision() with { Availability = availability }],
        };

        var result = await context.Reconciler.ReconcileAsync(
            AcceptanceDelivery(),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.TerminalFailure);
        result.Code.Should().Be("acceptance_artifact_revision_unavailable");
        context.Commands.Ready.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenEveryInvariantHolds_ShouldRecordTypedReadyEvidence()
    {
        var context = new TestContext();
        context.Runs.Items = [SuccessfulRun()];

        var result = await context.Reconciler.ReconcileAsync(
            AcceptanceDelivery(),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.Ready);
        result.Code.Should().Be("readiness_recording_accepted");
        context.Commands.Ready.Should().ContainSingle();
        var ready = context.Commands.Ready.Single();
        ready.Attempt.Should().Be(1);
        ready.OperationId.Should().Be(InstallationId);
        ready.ContinuationClaimId.Should().Be("claim-readiness-a1");
        ready.ContinuationClaimantId.Should().Be("worker-alpha");
        var evidence = ready.Evidence;
        evidence.PublishedService.Should().Be(new WorkflowPublishedServiceReadinessEvidence(
            ServiceId,
            Committed: true,
            Runnable: true,
            CommittedStateVersion: 11));
        evidence.BoundRevision.Should().Be(new WorkflowBoundRevisionReadinessEvidence(
            RevisionId,
            "bind-alpha",
            Bound: true,
            CommittedStateVersion: 12));
        evidence.Trigger.NoTrigger.Should().BeNull();
        evidence.Trigger.Schedule.Should().Be(new WorkflowScheduleReadinessEvidence(
            ScheduleId,
            "schedule-provisioning-alpha",
            WorkflowScheduleReadinessStatus.Ready,
            CommittedStateVersion: 31));
        evidence.AcceptanceRun.Should().Be(new WorkflowAcceptanceRunReadinessEvidence(
            "run-alpha",
            WorkflowAcceptanceRunStatus.TerminalSuccess,
            CommittedStateVersion: 21));
        evidence.Artifacts.Should().ContainSingle().Which.Should().Be(
            new WorkflowInstallationArtifactEvidence(
                WorkflowInstallationArtifactKind.RunOutput,
                ArtifactId,
                WorkflowInstallationArtifactVerificationStatus.Verified,
                $"content-artifact:{ArtifactId}:revision:{ArtifactRevisionId}",
                Digest));
        context.Runs.Queries.Should().ContainSingle().Which.Should().Match<ServiceRunQuery>(query =>
            query.ScopeId == ScopeId &&
            query.ServiceId == ServiceId &&
            query.ScheduleId == ScheduleId &&
            query.UpdatedFrom == DateTimeOffset.Parse("2026-08-16T01:00:00Z"));
        context.Artifacts.Gets.Should().ContainSingle().Which.Should().Be((ScopeId, ArtifactId));
    }

    [Fact]
    public async Task ReconcileAsync_WhenScheduleIdMaterializes_ShouldEnrichBeforeReady()
    {
        var context = new TestContext();
        context.Automations.ListResult = [Automation()];
        context.Runs.Items = [SuccessfulRun(scheduleId: ScheduleId)];

        var result = await context.Reconciler.ReconcileAsync(
            Delivery(WorkflowDeliveryTriggerKind.OneShot),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.Pending);
        result.Code.Should().Be("schedule_identity_enrichment_accepted");
        context.Commands.ProvisioningAccepted.Should().ContainSingle();
        var enrichment = context.Commands.ProvisioningAccepted.Single();
        enrichment.ScheduleId.Should().Be(ScheduleId);
        enrichment.Attempt.Should().Be(1);
        enrichment.OperationId.Should().Be(InstallationId);
        enrichment.ContinuationClaimId.Should().Be("claim-readiness-a1");
        enrichment.ContinuationClaimantId.Should().Be("worker-alpha");
        context.Commands.Ready.Should().BeEmpty();
        context.Runs.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenMatchingScheduleProvisioningFailed_ShouldReturnTypedTerminalFailure()
    {
        var context = new TestContext();
        context.Automations.ListResult = [];
        context.Member.Detail = MemberDetail(new StudioMemberWorkflowScheduleProvisioningStatusResponse(
            "schedule-provisioning-alpha",
            StudioWorkflowScheduleProvisioningStatusNames.Failed,
            RevisionId,
            ScheduleId: null,
            OperationId: null,
            AttemptCount: 1,
            StateVersion: 23,
            FailureCode: "NyxIdOperationAuthorityContractUnavailable",
            FailureMessage: "nyxid_operation_authority_contract_unavailable",
            UpdatedAt: DateTimeOffset.Parse("2026-08-16T01:07:00Z")));

        var result = await context.Reconciler.ReconcileAsync(
            Delivery(WorkflowDeliveryTriggerKind.OneShot),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.TerminalFailure);
        result.Code.Should().Be("NyxIdOperationAuthorityContractUnavailable");
        result.Message.Should().Be("nyxid_operation_authority_contract_unavailable");
        context.Member.DetailQueries.Should().ContainSingle().Which.Should().Be((ScopeId, MemberId));
        context.Commands.ProvisioningAccepted.Should().BeEmpty();
        context.Commands.Ready.Should().BeEmpty();
        context.Runs.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenScheduledRunUsesDifferentSchedule_ShouldNotRecordReady()
    {
        var context = new TestContext();
        context.Automations.GetResult = Automation();
        context.Runs.Items = [SuccessfulRun(scheduleId: "schedule-other")];

        var result = await context.Reconciler.ReconcileAsync(
            Delivery(WorkflowDeliveryTriggerKind.OneShot, scheduleId: ScheduleId),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.Pending);
        result.Code.Should().Be("acceptance_run_pending");
        context.Commands.Ready.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenScheduledRunMatchesExactSchedule_ShouldRecordReady()
    {
        var context = new TestContext();
        context.Automations.GetResult = Automation();
        context.Runs.Items = [SuccessfulRun(scheduleId: ScheduleId)];

        var result = await context.Reconciler.ReconcileAsync(
            Delivery(WorkflowDeliveryTriggerKind.OneShot, scheduleId: ScheduleId),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.Ready);
        context.Commands.Ready.Should().ContainSingle();
        context.Commands.Ready.Single().Evidence.Trigger.Schedule.Should().Be(
            new WorkflowScheduleReadinessEvidence(
                ScheduleId,
                "schedule-provisioning-alpha",
                WorkflowScheduleReadinessStatus.Ready,
                CommittedStateVersion: 31));
    }

    [Fact]
    public async Task ReconcileAsync_WhenScheduledRunBelongsToOldAttempt_ShouldRemainPending()
    {
        var context = new TestContext();
        context.Automations.GetResult = Automation();
        context.Runs.Items =
        [
            SuccessfulRun(scheduleId: ScheduleId) with
            {
                ScheduleOperationId = "installation-alpha:provision:a0",
            },
        ];

        var result = await context.Reconciler.ReconcileAsync(
            Delivery(WorkflowDeliveryTriggerKind.OneShot, scheduleId: ScheduleId),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.Pending);
        result.Code.Should().Be("acceptance_run_pending");
        context.Commands.Ready.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenArtifactProvenanceDiffers_ShouldReturnTerminalFailure()
    {
        var context = new TestContext();
        context.Runs.Items = [SuccessfulRun()];
        context.Artifacts.Current = ArtifactCurrent() with
        {
            Revisions =
            [
                ArtifactRevision() with
                {
                    Provenance = ArtifactRevision().Provenance with { WorkflowId = "wf-other" },
                },
            ],
        };

        var result = await context.Reconciler.ReconcileAsync(
            AcceptanceDelivery(),
            ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.TerminalFailure);
        result.Code.Should().Be("acceptance_artifact_provenance_mismatch");
        context.Commands.Ready.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenInstallationHasNoTrigger_ShouldRecordReadyWithoutRunOrArtifact()
    {
        var context = new TestContext();
        context.Runs.Items =
        [
            SuccessfulRun(scheduleId: string.Empty) with { ScheduleOperationId = string.Empty },
        ];

        var result = await context.Reconciler.ReconcileAsync(Delivery(), ContinuationClaimantId);

        result.Status.Should().Be(WorkflowInstallationReadinessReconciliationStatus.Ready);
        result.Code.Should().Be("readiness_recording_accepted");
        context.Commands.Ready.Should().ContainSingle();
        context.Commands.Ready.Single().Evidence.Trigger.NoTrigger.Should()
            .Be(new WorkflowNoTriggerReadinessEvidence(Ready: true));
        context.Commands.Ready.Single().Evidence.AcceptanceRun.Should().BeNull();
        context.Commands.Ready.Single().Evidence.Artifacts.Should().BeEmpty();
        context.Runs.Queries.Should().BeEmpty();
        context.Artifacts.Gets.Should().BeEmpty();
    }

    private static WorkflowDeliverySnapshot AcceptanceDelivery() =>
        Delivery(WorkflowDeliveryTriggerKind.OneShot, ScheduleId);

    private static WorkflowDeliverySnapshot Delivery(
        WorkflowDeliveryTriggerKind triggerKind = WorkflowDeliveryTriggerKind.None,
        string? scheduleId = null)
    {
        var now = DateTimeOffset.Parse("2026-08-16T01:00:00Z");
        var trigger = triggerKind switch
        {
            WorkflowDeliveryTriggerKind.OneShot =>
                new WorkflowDeliveryTriggerIntent(triggerKind, null, "UTC", RunImmediately: true),
            WorkflowDeliveryTriggerKind.Cron =>
                new WorkflowDeliveryTriggerIntent(triggerKind, "0 9 * * *", "UTC", RunImmediately: false),
            _ => new WorkflowDeliveryTriggerIntent(
                WorkflowDeliveryTriggerKind.None,
                null,
                null,
                RunImmediately: false),
        };
        var installation = new WorkflowInstallationSnapshot(
            InstallationId,
            "idempotency-alpha",
            ScopeId,
            TeamId,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            trigger,
            "source-hash",
            "resolved-hash",
            "name: workflow-alpha\nsteps: []\n",
            [],
            new WorkflowCapabilityAdmissionPlan(),
            AuthenticatedOwner: new AuthenticatedAuthorizationOwnerContext(
                new AuthorizationOwnerIdentity
                {
                    Authority = "nyxid",
                    OwnerKind = AuthorizationOwnerKind.Personal,
                    OwnerSubject = "caller-alpha",
                },
                "nyxid",
                string.Empty,
                "user-alpha",
                "binding-alpha"),
            AcceptanceInput: new Struct(),
            OperationId: InstallationId,
            WorkflowInstallationStatus.ProvisioningAccepted,
            "provisioning_accepted",
            ErrorCode: null,
            ErrorMessage: null,
            "wf-alpha",
            MemberId,
            ServiceId,
            RevisionId,
            "bind-alpha",
            scheduleId,
            triggerKind == WorkflowDeliveryTriggerKind.None ? null : "schedule-provisioning-alpha",
            triggerKind == WorkflowDeliveryTriggerKind.None ? null : "pending_binding",
            ReadinessEvidence: null,
            Attempt: 1,
            now,
            now)
        {
            ContinuationClaim = new WorkflowInstallationContinuationClaimSnapshot(
                "claim-readiness-a1",
                ContinuationClaimantId,
                WorkflowInstallationStatus.ProvisioningAccepted,
                1,
                InstallationId,
                now.AddMinutes(9),
                now.AddMinutes(12)),
        };
        return new WorkflowDeliverySnapshot(
            "delivery-alpha",
            new WorkflowDeliveryPackageSnapshot(
                "package-alpha",
                "package-version-alpha",
                WorkflowName,
                "1",
                "Workflow Alpha",
                "Description",
                "name: workflow-alpha\nsteps: []\n",
                "source-hash",
                "package-hash",
                [],
                [],
                [],
                "Risk",
                [],
                new WorkflowDeliveryAcceptancePolicy(
                    WorkflowDeliveryAcceptanceMode.AutomaticPreview,
                    null,
                    new WorkflowDeliveryAcceptanceInputRecipe(new Struct(), [])),
                "admin-alpha",
                now),
            ScopeId,
            now.AddDays(7),
            null,
            WorkflowDeliveryLifecycleStatus.Active,
            "admin-alpha",
            now,
            now,
            null,
            null,
            [],
            installation,
            4,
            now);
    }

    private static StudioMemberEndpointContractResponse EndpointContract(
        string publishedServiceId = ServiceId,
        string revisionId = RevisionId) =>
        new(
            ScopeId,
            MemberId,
            publishedServiceId,
            WorkflowInstallationReadinessReconciler.WorkflowEndpointId,
            "/api/scopes/scope-alpha/members/m-alpha/invoke/chat:stream",
            "POST",
            "application/json",
            "text/event-stream",
            "type.googleapis.com/aevatar.workflow.ChatRequestEvent",
            "type.googleapis.com/aevatar.workflow.ChatResponseEvent",
            SupportsSse: true,
            SupportsWebSocket: false,
            SupportsAguiFrames: true,
            StreamFrameFormat: "agui",
            SmokeTestSupported: true,
            DefaultSmokeInputMode: "prompt",
            DefaultSmokePrompt: "hello",
            SampleRequestJson: null,
            DeploymentStatus: ServiceDeploymentStatus.Active.ToString(),
            revisionId,
            new StudioMemberInvocationReadinessResponse(
                CanInvoke: true,
                StudioMemberInvocationReadinessStatusNames.Ready,
                StudioMemberInvocationReadinessStatusNames.Ready,
                "Ready",
                revisionId))
        {
            PublishedServiceStateVersion = 11,
            BoundRevisionStateVersion = 12,
        };

    private static StudioMemberDetailResponse MemberDetail(
        StudioMemberWorkflowScheduleProvisioningStatusResponse? scheduleProvisioning = null) =>
        new(
            new StudioMemberSummaryResponse(
                MemberId,
                ScopeId,
                "Workflow Alpha",
                "Description",
                "workflow",
                "bind_ready",
                ServiceId,
                RevisionId,
                DateTimeOffset.Parse("2026-08-16T01:00:00Z"),
                DateTimeOffset.Parse("2026-08-16T01:07:00Z")),
            ImplementationRef: null,
            LastBinding: null)
        {
            ScheduleProvisioning = scheduleProvisioning,
        };

    private static StudioMemberAutomationView Automation(string targetRevisionId = RevisionId) =>
        new(
            ScopeId,
            TeamId,
            MemberId,
            ScheduleId,
            ServiceId,
            "Workflow Alpha",
            string.Empty,
            string.Empty,
            "UTC",
            Enabled: false,
            AuthorizationStatus: "active",
            CredentialExpiresAtUtc: DateTimeOffset.Parse("2026-12-31T00:00:00Z"),
            LastAuthorizationErrorCode: string.Empty,
            OperationId: InstallationId,
            CredentialGeneration: 1,
            RevocationPending: false,
            NextFireAt: null,
            LastFireAt: DateTimeOffset.Parse("2026-08-16T01:05:00Z"),
            StateVersion: 31)
        {
            TargetRevisionId = targetRevisionId,
            UpdatedAt = DateTimeOffset.Parse("2026-08-16T01:06:00Z"),
        };

    private static ServiceRunSnapshot SuccessfulRun(string scheduleId = ScheduleId) =>
        Run(ServiceRunStatus.Completed, scheduleId) with
        {
            ScheduleOperationId = scheduleId.Length == 0 ? string.Empty : InstallationId,
            ResultArtifacts =
            [
                new ContentArtifactReference
                {
                    ArtifactId = ArtifactId,
                    RevisionId = ArtifactRevisionId,
                    ContentHash = Digest,
                    MediaType = "application/json",
                },
            ],
        };

    private static ServiceRunSnapshot Run(ServiceRunStatus status, string scheduleId = ScheduleId) =>
        new(
            ScopeId,
            ServiceId,
            "service-key-alpha",
            "run-alpha",
            "command-alpha",
            "correlation-alpha",
            "chat",
            scheduleId,
            ServiceImplementationKind.Workflow,
            "actor-alpha",
            RevisionId,
            "deployment-alpha",
            status,
            "service-run:run-alpha",
            ScopeId,
            "app-alpha",
            "namespace-alpha",
            StateVersion: 21,
            "event-alpha",
            DateTimeOffset.Parse("2026-08-16T01:04:00Z"),
            DateTimeOffset.Parse("2026-08-16T01:05:00Z"),
            AcceptanceOutput,
            status == ServiceRunStatus.Failed ? "acceptance failed" : string.Empty)
        {
            ScheduleOperationId = scheduleId.Length == 0 ? string.Empty : InstallationId,
        };

    private static ContentArtifactCurrentStateResponse ArtifactCurrent() =>
        new(
            ArtifactId,
            ScopeId,
            TeamId,
            "structured_document",
            "Acceptance output",
            "internal",
            ContentArtifactLifecycleStatusNames.Active,
            ArtifactRevisionId,
            ConcurrencyVersion: 1,
            StateVersion: 41,
            new ContentArtifactPrincipalContract("caller-alpha", "user"),
            [],
            [],
            RetentionPolicy: null,
            WorkOrderId: null,
            [ArtifactRevision()],
            DateTimeOffset.Parse("2026-08-16T01:04:00Z"),
            DateTimeOffset.Parse("2026-08-16T01:05:00Z"));

    private static ContentArtifactRevisionResponse ArtifactRevision(
        string? contentHash = null,
        string mediaType = "application/json") =>
        new(
            ArtifactRevisionId,
            RevisionNumber: 1,
            ParentRevisionId: null,
            mediaType,
            ByteLength: System.Text.Encoding.UTF8.GetByteCount(AcceptanceOutput),
            contentHash ?? Digest,
            ContentArtifactRevisionAvailabilityNames.Available,
            HasInlineContent: true,
            HasBackingContent: false,
            new ContentArtifactExecutionProvenanceContract(
                ScopeId,
                TeamId,
                MemberId,
                "wf-alpha",
                ServiceId,
                "run-alpha"),
            [],
            DateTimeOffset.Parse("2026-08-16T01:04:00Z"));

    private static WorkflowAcceptanceArtifactIdentity AcceptanceIdentity() =>
        WorkflowAcceptanceArtifactContract.BuildIdentity(AcceptanceDelivery(), "run-alpha");

    private static WorkflowAcceptanceOutputValidation AcceptanceOutputValidation() =>
        WorkflowAcceptanceArtifactContract.ValidateOutput(WorkflowName, AcceptanceOutput);

    private sealed class TestContext
    {
        public StubMemberService Member { get; } = new();
        public StubAutomationQueryPort Automations { get; } = new();
        public StubServiceRunQueryPort Runs { get; } = new();
        public WorkflowCurrentStateQueryPortStub WorkflowStates { get; } = new();
        public StubContentArtifactQueryPort Artifacts { get; } = new();
        public RecordingCommandPort Commands { get; } = new();

        public WorkflowInstallationReadinessReconciler Reconciler { get; }

        public TestContext()
        {
            Member.Contract = EndpointContract();
            Automations.GetResult = Automation();
            Artifacts.Current = ArtifactCurrent();
            WorkflowStates.Fallback = actorId => Runs.Items
                .FirstOrDefault(run => string.Equals(run.TargetActorId, actorId, StringComparison.Ordinal)) is { } run
                    ? WorkflowCurrentStateQueryPortStub.FromServiceRun(run)
                    : null;
            Reconciler = new WorkflowInstallationReadinessReconciler(
                Member,
                Automations,
                Runs,
                WorkflowStates,
                Artifacts,
                Commands,
                new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T01:10:00Z")));
        }
    }

    private sealed class StubMemberService : IStudioMemberService
    {
        public StudioMemberEndpointContractResponse? Contract { get; set; }
        public StudioMemberDetailResponse Detail { get; set; } = MemberDetail();
        public List<(string ScopeId, string MemberId, string EndpointId)> EndpointContractQueries { get; } = [];
        public List<(string ScopeId, string MemberId)> DetailQueries { get; } = [];

        public Task<StudioMemberEndpointContractResponse?> GetEndpointContractAsync(
            string scopeId,
            string memberId,
            string endpointId,
            CancellationToken ct = default)
        {
            EndpointContractQueries.Add((scopeId, memberId, endpointId));
            return Task.FromResult(Contract);
        }

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId,
            CreateStudioMemberRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StudioMemberDetailResponse> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default)
        {
            DetailQueries.Add((scopeId, memberId));
            return Task.FromResult(Detail);
        }

        public Task<StudioMemberBindingAcceptedResponse> BindAsync(
            string scopeId,
            string memberId,
            UpdateStudioMemberBindingRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StudioMemberBindingViewResponse> GetBindingAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StudioMemberBindingRunStatusResponse> GetBindingRunAsync(
            string scopeId,
            string memberId,
            string bindingRunId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StudioMemberBindingActivationResponse> ActivateBindingRevisionAsync(
            string scopeId,
            string memberId,
            string revisionId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StudioMemberBindingRevisionActionResponse> RetireBindingRevisionAsync(
            string scopeId,
            string memberId,
            string revisionId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StudioMemberCommandResponse> UpdateAsync(
            string scopeId,
            string memberId,
            UpdateStudioMemberRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StudioMemberCommandResponse> DeleteAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubAutomationQueryPort : IStudioMemberAutomationQueryPort
    {
        public StudioMemberAutomationView? GetResult { get; set; }
        public IReadOnlyList<StudioMemberAutomationView> ListResult { get; set; } = [];
        public List<(string ScopeId, string TeamId, string? MemberId)> ListQueries { get; } = [];
        public List<(string ScopeId, string TeamId, string MemberId, string ScheduleId)> GetQueries { get; } = [];

        public Task<StudioMemberAutomationListResponse> ListAsync(
            string scopeId,
            string teamId,
            string? memberId,
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default)
        {
            ListQueries.Add((scopeId, teamId, memberId));
            return Task.FromResult(new StudioMemberAutomationListResponse(ListResult, null, null));
        }

        public Task<StudioMemberAutomationView?> GetAsync(
            string scopeId,
            string teamId,
            string memberId,
            string scheduleId,
            CancellationToken ct = default)
        {
            GetQueries.Add((scopeId, teamId, memberId, scheduleId));
            return Task.FromResult(GetResult);
        }
    }

    private sealed class StubServiceRunQueryPort : IServiceRunQueryPort
    {
        public IReadOnlyList<ServiceRunSnapshot> Items { get; set; } = [];
        public List<ServiceRunQuery> Queries { get; } = [];

        public Task<IReadOnlyList<ServiceRunSnapshot>> ListAsync(
            ServiceRunQuery query,
            CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult(Items);
        }

        public Task<ServiceRunSnapshot?> GetByRunIdAsync(
            string scopeId,
            string serviceId,
            string runId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServiceRunSnapshot?> GetByCommandIdAsync(
            string scopeId,
            string serviceId,
            string commandId,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubContentArtifactQueryPort : IContentArtifactQueryPort
    {
        public ContentArtifactCurrentStateResponse? Current { get; set; }
        public byte[] Content { get; set; } = System.Text.Encoding.UTF8.GetBytes(AcceptanceOutput);
        public List<(string ScopeId, string ArtifactId)> Gets { get; } = [];

        public Task<ContentArtifactCurrentStateResponse?> GetAsync(
            string scopeId,
            string artifactId,
            CancellationToken ct = default)
        {
            Gets.Add((scopeId, artifactId));
            return Task.FromResult(Current);
        }

        public Task<ContentArtifactListResponse> ListAsync(
            string scopeId,
            string requesterPrincipalId,
            ContentArtifactQueryRequest query,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ContentArtifactCurrentStateResponse?> GetByDedupKeyAsync(
            string scopeId,
            string dedupKey,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ContentArtifactRevisionContentResponse> GetRevisionContentAsync(
            string scopeId,
            string artifactId,
            string revisionId,
            ContentArtifactPrincipalContract requester,
            CancellationToken ct = default) =>
            Task.FromResult(new ContentArtifactRevisionContentResponse(
                new ContentArtifactReferenceContract(
                    artifactId,
                    revisionId,
                    Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Content)),
                    WorkflowAcceptanceArtifactContract.MediaType),
                Content));
    }

    private sealed class RecordingCommandPort : IWorkflowDeliveryCommandPort
    {
        public List<RecordWorkflowProvisioningAcceptedMutation> ProvisioningAccepted { get; } = [];
        public List<RecordWorkflowInstallationReadyMutation> Ready { get; } = [];

        public Task<WorkflowDeliveryCommandReceipt> RecordProvisioningAcceptedAsync(
            RecordWorkflowProvisioningAcceptedMutation mutation,
            CancellationToken ct = default)
        {
            ProvisioningAccepted.Add(mutation);
            return Accepted(mutation.DeliveryId);
        }

        public Task<WorkflowDeliveryCommandReceipt> RecordInstallationReadyAsync(
            RecordWorkflowInstallationReadyMutation mutation,
            CancellationToken ct = default)
        {
            Ready.Add(mutation);
            return Accepted(mutation.DeliveryId);
        }

        public Task<WorkflowDeliveryCommandReceipt> CreateAsync(
            CreateWorkflowDeliveryMutation mutation,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkflowDeliveryCommandReceipt> RecordAccessAsync(
            RecordWorkflowDeliveryAccessMutation mutation,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkflowDeliveryCommandReceipt> RevokeAsync(
            RevokeWorkflowDeliveryMutation mutation,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkflowDeliveryCommandReceipt> BeginConnectionAsync(
            BeginWorkflowDeliveryConnectionMutation mutation,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkflowDeliveryCommandReceipt> UpdateConnectionAsync(
            UpdateWorkflowDeliveryConnectionMutation mutation,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkflowDeliveryCommandReceipt> AttachConnectionAsync(
            AttachWorkflowDeliveryConnectionMutation mutation,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkflowDeliveryCommandReceipt> StartInstallationAsync(
            StartWorkflowInstallationMutation mutation,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkflowDeliveryCommandReceipt> RetryInstallationAsync(
            RetryWorkflowInstallationMutation mutation,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkflowDeliveryCommandReceipt> ClaimInstallationContinuationAsync(
            ClaimWorkflowInstallationContinuationMutation mutation,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkflowDeliveryCommandReceipt> RecordInstallationFailedAsync(
            RecordWorkflowInstallationFailedMutation mutation,
            CancellationToken ct = default) => throw new NotSupportedException();

        private static Task<WorkflowDeliveryCommandReceipt> Accepted(string deliveryId) =>
            Task.FromResult(new WorkflowDeliveryCommandReceipt(
                deliveryId,
                $"workflow-delivery:{deliveryId}",
                "command-alpha",
                "correlation-alpha",
                WorkflowDeliveryCommandAckStage.AcceptedForDispatch,
                null));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
