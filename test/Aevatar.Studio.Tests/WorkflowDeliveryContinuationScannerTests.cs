using Aevatar.Studio.Application.Delivery;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting.WorkflowDeliveries;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowDeliveryContinuationScannerTests
{
    [Fact]
    public async Task ScanOnceAsync_AfterRestart_ShouldPageAcceptedAndInvokeReadinessReconciliation()
    {
        var now = DateTimeOffset.Parse("2026-08-16T06:00:00Z");
        var queries = new PagedQueryPort(now);
        var provisioning = new RecordingProvisioningExecutor();
        var continuationCalls = new List<string>();
        var materializer = new RecordingArtifactMaterializer(calls: continuationCalls);
        var readiness = new RecordingReadinessReconciler(calls: continuationCalls);
        var commands = new RecordingCommandPort();
        var scanner = new WorkflowDeliveryContinuationScanner(
            queries,
            provisioning,
            materializer,
            readiness,
            commands,
            new FixedTimeProvider(now),
            NullLogger<WorkflowDeliveryContinuationScanner>.Instance,
            WorkerOptions(pageSize: 1));

        await scanner.ScanOnceAsync();

        provisioning.Deliveries.Select(static item => item.DeliveryId).Should()
            .Equal("delivery-alpha", "delivery-beta");
        readiness.Deliveries.Select(static item => item.DeliveryId).Should()
            .Equal("delivery-gamma");
        materializer.Deliveries.Select(static item => item.DeliveryId).Should()
            .Equal("delivery-gamma");
        continuationCalls.Should().Equal(
            "materialize:delivery-gamma",
            "readiness:delivery-gamma");
        queries.Queries.Should().Equal(
            new QueryCall(WorkflowInstallationStatus.Accepted, null),
            new QueryCall(WorkflowInstallationStatus.Accepted, "accepted-page-2"),
            new QueryCall(WorkflowInstallationStatus.ProvisioningAccepted, null));
        commands.Failures.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkflowInstallationStatus.Accepted)]
    [InlineData(WorkflowInstallationStatus.ProvisioningAccepted)]
    public async Task ScanOnceAsync_UnclaimedContinuation_ShouldDispatchClaimAndWaitForCommittedReadModel(
        WorkflowInstallationStatus status)
    {
        var now = DateTimeOffset.Parse("2026-08-16T06:00:00Z");
        var delivery = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            status,
            pageSuffix: status == WorkflowInstallationStatus.Accepted ? "claim-accepted" : "claim-readiness",
            includeContinuationClaim: false);
        var queries = new MutableSingleDeliveryQueryPort(delivery);
        var provisioning = new RecordingProvisioningExecutor();
        var materializer = new RecordingArtifactMaterializer();
        var readiness = new RecordingReadinessReconciler();
        var commands = new RecordingCommandPort();
        var scanner = new WorkflowDeliveryContinuationScanner(
            queries,
            provisioning,
            materializer,
            readiness,
            commands,
            new FixedTimeProvider(now),
            NullLogger<WorkflowDeliveryContinuationScanner>.Instance,
            WorkerOptions());

        await scanner.ScanOnceAsync();

        provisioning.Deliveries.Should().BeEmpty();
        materializer.Deliveries.Should().BeEmpty();
        readiness.Deliveries.Should().BeEmpty();
        var claim = commands.Claims.Should().ContainSingle().Subject;
        claim.ExpectedStatus.Should().Be(status);
        claim.ClaimantId.Should().Be("worker-alpha");

        queries.Delivery = delivery with
        {
            Installation = delivery.Installation! with
            {
                ContinuationClaim = new WorkflowInstallationContinuationClaimSnapshot(
                    claim.ClaimId,
                    claim.ClaimantId,
                    claim.ExpectedStatus,
                    claim.Attempt,
                    claim.OperationId,
                    now,
                    now.Add(claim.RequestedDuration)),
            },
        };
        await scanner.ScanOnceAsync();

        commands.Claims.Should().ContainSingle();
        if (status == WorkflowInstallationStatus.Accepted)
        {
            provisioning.Deliveries.Should().ContainSingle();
            materializer.Deliveries.Should().BeEmpty();
            readiness.Deliveries.Should().BeEmpty();
        }
        else
        {
            provisioning.Deliveries.Should().BeEmpty();
            materializer.Deliveries.Should().ContainSingle();
            readiness.Deliveries.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task ScanOnceAsync_ClaimOwnedByAnotherReplica_ShouldNeitherExecuteNorStealUntilExpiry()
    {
        var now = DateTimeOffset.Parse("2026-08-16T06:00:00Z");
        var delivery = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.Accepted,
            pageSuffix: "other-owner",
            claimantId: "worker-beta",
            claimAtUtc: now.AddMinutes(-1),
            claimExpiresAtUtc: now.AddMinutes(1));
        var provisioning = new RecordingProvisioningExecutor();
        var commands = new RecordingCommandPort();
        var scanner = new WorkflowDeliveryContinuationScanner(
            new MutableSingleDeliveryQueryPort(delivery),
            provisioning,
            new RecordingArtifactMaterializer(),
            new RecordingReadinessReconciler(),
            commands,
            new FixedTimeProvider(now),
            NullLogger<WorkflowDeliveryContinuationScanner>.Instance,
            WorkerOptions());

        await scanner.ScanOnceAsync();

        provisioning.Deliveries.Should().BeEmpty();
        commands.Claims.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkflowInstallationStatus.Accepted)]
    [InlineData(WorkflowInstallationStatus.ProvisioningAccepted)]
    public async Task ScanOnceAsync_ActorIssuedClaimFromBeforeClockRollback_ShouldRemainExclusiveAndExecute(
        WorkflowInstallationStatus status)
    {
        var now = DateTimeOffset.Parse("2026-08-16T06:00:00Z");
        var delivery = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            status,
            pageSuffix: "future-claim",
            claimantId: "worker-alpha",
            claimAtUtc: now.AddMinutes(1),
            claimExpiresAtUtc: now.AddMinutes(2));
        var provisioning = new RecordingProvisioningExecutor();
        var materializer = new RecordingArtifactMaterializer();
        var readiness = new RecordingReadinessReconciler();
        var commands = new RecordingCommandPort();
        var scanner = new WorkflowDeliveryContinuationScanner(
            new MutableSingleDeliveryQueryPort(delivery),
            provisioning,
            materializer,
            readiness,
            commands,
            new FixedTimeProvider(now),
            NullLogger<WorkflowDeliveryContinuationScanner>.Instance,
            WorkerOptions());

        await scanner.ScanOnceAsync();

        commands.Claims.Should().BeEmpty();
        if (status == WorkflowInstallationStatus.Accepted)
        {
            provisioning.Deliveries.Should().ContainSingle();
            materializer.Deliveries.Should().BeEmpty();
            readiness.Deliveries.Should().BeEmpty();
        }
        else
        {
            provisioning.Deliveries.Should().BeEmpty();
            materializer.Deliveries.Should().ContainSingle();
            readiness.Deliveries.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task ScanOnceAsync_WhenDeliveryExpiresBeforeDefaultLease_ShouldRequestConfiguredDurationForActorCap()
    {
        var now = DateTimeOffset.Parse("2026-08-16T06:00:00Z");
        var delivery = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.Accepted,
            pageSuffix: "near-expiry",
            includeContinuationClaim: false) with
        {
            ExpiresAtUtc = now.AddSeconds(30),
        };
        var commands = new RecordingCommandPort();
        var scanner = new WorkflowDeliveryContinuationScanner(
            new MutableSingleDeliveryQueryPort(delivery),
            new RecordingProvisioningExecutor(),
            new RecordingArtifactMaterializer(),
            new RecordingReadinessReconciler(),
            commands,
            new FixedTimeProvider(now),
            NullLogger<WorkflowDeliveryContinuationScanner>.Instance,
            WorkerOptions());

        await scanner.ScanOnceAsync();

        commands.Claims.Should().ContainSingle().Which.RequestedDuration.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task ScanOnceAsync_WhenOneClaimDispatchFails_ShouldContinueClaimingLaterDeliveries()
    {
        var now = DateTimeOffset.Parse("2026-08-16T06:00:00Z");
        var first = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.Accepted,
            pageSuffix: "claim-fails",
            includeContinuationClaim: false);
        var second = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.Accepted,
            pageSuffix: "claim-continues",
            includeContinuationClaim: false);
        var commands = new RecordingCommandPort
        {
            FailingClaimDeliveryId = first.DeliveryId,
        };
        var scanner = new WorkflowDeliveryContinuationScanner(
            new AcceptedDeliveriesQueryPort([first, second]),
            new RecordingProvisioningExecutor(),
            new RecordingArtifactMaterializer(),
            new RecordingReadinessReconciler(),
            commands,
            new FixedTimeProvider(now),
            NullLogger<WorkflowDeliveryContinuationScanner>.Instance,
            WorkerOptions());

        await scanner.ScanOnceAsync();

        commands.Claims.Select(static claim => claim.DeliveryId)
            .Should().Equal(first.DeliveryId, second.DeliveryId);
    }

    [Fact]
    public async Task ScanOnceAsync_WhenContinuationReachesHardDeadline_ShouldCancelItAndContinueLaterItems()
    {
        var now = DateTimeOffset.Parse("2026-08-16T06:00:00Z");
        var clock = new FakeTimeProvider(now);
        var first = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.Accepted,
            pageSuffix: "slow",
            claimantId: "worker-alpha",
            claimAtUtc: now,
            claimExpiresAtUtc: now.AddSeconds(5));
        var second = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.Accepted,
            pageSuffix: "after-slow",
            claimantId: "worker-alpha",
            claimAtUtc: now,
            claimExpiresAtUtc: now.AddSeconds(10));
        var provisioning = new DeadlineBlockingProvisioningExecutor(first.DeliveryId);
        var scanner = new WorkflowDeliveryContinuationScanner(
            new AcceptedDeliveriesQueryPort([first, second]),
            provisioning,
            new RecordingArtifactMaterializer(),
            new RecordingReadinessReconciler(),
            new RecordingCommandPort(),
            clock,
            NullLogger<WorkflowDeliveryContinuationScanner>.Instance,
            WorkerOptions());

        var scan = scanner.ScanOnceAsync();
        await provisioning.BlockingAttemptStarted.Task;
        clock.Advance(TimeSpan.FromSeconds(4));
        await scan;

        provisioning.CancellationObserved.Should().BeTrue();
        provisioning.Attempts.Should().Equal(first.DeliveryId, second.DeliveryId);
        provisioning.Succeeded.Should().Equal(second.DeliveryId);
    }

    [Fact]
    public async Task ScanOnceAsync_WhenReadinessIsTerminalFailure_ShouldPersistFencedInstallationFailure()
    {
        var delivery = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.ProvisioningAccepted,
            pageSuffix: "terminal",
            claimantId: "worker-alpha",
            claimAtUtc: DateTimeOffset.Parse("2026-08-16T06:29:00Z"),
            claimExpiresAtUtc: DateTimeOffset.Parse("2026-08-16T06:31:00Z"));
        var queries = new SingleDeliveryQueryPort(delivery);
        var commands = new RecordingCommandPort();
        var failedAt = DateTimeOffset.Parse("2026-08-16T06:30:00Z");
        var scanner = new WorkflowDeliveryContinuationScanner(
            queries,
            new RecordingProvisioningExecutor(),
            new RecordingArtifactMaterializer(),
            new RecordingReadinessReconciler(new WorkflowInstallationReadinessReconciliationResult(
                WorkflowInstallationReadinessReconciliationStatus.TerminalFailure,
                "acceptance_run_failed",
                "The acceptance run failed.")),
            commands,
            new FixedTimeProvider(failedAt),
            NullLogger<WorkflowDeliveryContinuationScanner>.Instance,
            WorkerOptions(pageSize: 10));

        await scanner.ScanOnceAsync();

        var failure = commands.Failures.Should().ContainSingle().Subject;
        failure.DeliveryId.Should().Be(delivery.DeliveryId);
        failure.InstallationId.Should().Be(delivery.Installation!.InstallationId);
        failure.ErrorCode.Should().Be("acceptance_run_failed");
        failure.ErrorMessage.Should().Be("The acceptance run failed.");
        failure.ExpectedStatus.Should().Be(WorkflowInstallationStatus.ProvisioningAccepted);
        failure.Attempt.Should().Be(delivery.Installation.Attempt);
        failure.OperationId.Should().Be(delivery.Installation.OperationId);
        failure.FailedAtUtc.Should().Be(failedAt);
        failure.ContinuationClaimId.Should().Be("claim-terminal");
        failure.ContinuationClaimantId.Should().Be("worker-alpha");
    }

    [Fact]
    public async Task ScanOnceAsync_WhenArtifactMaterializationIsTerminalFailure_ShouldPersistFencedFailureWithoutReadiness()
    {
        var delivery = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.ProvisioningAccepted,
            pageSuffix: "artifact-terminal",
            claimantId: "worker-alpha",
            claimAtUtc: DateTimeOffset.Parse("2026-08-16T06:44:00Z"),
            claimExpiresAtUtc: DateTimeOffset.Parse("2026-08-16T06:46:00Z"));
        var commands = new RecordingCommandPort();
        var readiness = new RecordingReadinessReconciler();
        var failedAt = DateTimeOffset.Parse("2026-08-16T06:45:00Z");
        var scanner = new WorkflowDeliveryContinuationScanner(
            new SingleDeliveryQueryPort(delivery),
            new RecordingProvisioningExecutor(),
            new RecordingArtifactMaterializer(new WorkflowAcceptanceArtifactMaterializationResult(
                WorkflowAcceptanceArtifactMaterializationStatus.TerminalFailure,
                "acceptance_output_contract_invalid",
                "The acceptance output is invalid.")),
            readiness,
            commands,
            new FixedTimeProvider(failedAt),
            NullLogger<WorkflowDeliveryContinuationScanner>.Instance,
            WorkerOptions(pageSize: 10));

        await scanner.ScanOnceAsync();

        readiness.Deliveries.Should().BeEmpty();
        var failure = commands.Failures.Should().ContainSingle().Subject;
        failure.DeliveryId.Should().Be(delivery.DeliveryId);
        failure.InstallationId.Should().Be(delivery.Installation!.InstallationId);
        failure.ErrorCode.Should().Be("acceptance_output_contract_invalid");
        failure.ErrorMessage.Should().Be("The acceptance output is invalid.");
        failure.ExpectedStatus.Should().Be(WorkflowInstallationStatus.ProvisioningAccepted);
        failure.Attempt.Should().Be(delivery.Installation.Attempt);
        failure.OperationId.Should().Be(delivery.Installation.OperationId);
        failure.FailedAtUtc.Should().Be(failedAt);
        failure.ContinuationClaimId.Should().Be("claim-artifact-terminal");
        failure.ContinuationClaimantId.Should().Be("worker-alpha");
    }

    [Fact]
    public async Task ScanOnceAsync_WhenOneContinuationFails_ShouldContinueWithLaterDeliveries()
    {
        var first = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.Accepted,
            pageSuffix: "fails",
            claimantId: "worker-alpha",
            claimAtUtc: DateTimeOffset.Parse("2026-08-16T06:59:00Z"),
            claimExpiresAtUtc: DateTimeOffset.Parse("2026-08-16T07:01:00Z"));
        var second = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.Accepted,
            pageSuffix: "continues",
            claimantId: "worker-alpha",
            claimAtUtc: DateTimeOffset.Parse("2026-08-16T06:59:00Z"),
            claimExpiresAtUtc: DateTimeOffset.Parse("2026-08-16T07:01:00Z"));
        var provisioning = new FailingFirstProvisioningExecutor(first.DeliveryId);
        var scanner = new WorkflowDeliveryContinuationScanner(
            new AcceptedDeliveriesQueryPort([first, second]),
            provisioning,
            new RecordingArtifactMaterializer(),
            new RecordingReadinessReconciler(),
            new RecordingCommandPort(),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T07:00:00Z")),
            NullLogger<WorkflowDeliveryContinuationScanner>.Instance,
            WorkerOptions(pageSize: 10));

        await scanner.ScanOnceAsync();

        provisioning.Attempts.Should().Equal(first.DeliveryId, second.DeliveryId);
        provisioning.Succeeded.Should().Equal(second.DeliveryId);
    }

    private sealed class PagedQueryPort(DateTimeOffset now) : IWorkflowDeliveryQueryPort
    {
        public List<QueryCall> Queries { get; } = [];

        public Task<WorkflowDeliveryListResult> ListAsync(
            WorkflowDeliveryListQuery query,
            CancellationToken ct = default)
        {
            Queries.Add(new QueryCall(query.InstallationStatus, query.PageToken));
            return (query.InstallationStatus, query.PageToken) switch
            {
                (WorkflowInstallationStatus.Accepted, null) => Task.FromResult(
                    new WorkflowDeliveryListResult(
                        [WorkflowDeliveryProvisioningExecutorTests.Delivery(
                            WorkflowInstallationStatus.Accepted,
                            pageSuffix: "alpha",
                            claimantId: "worker-alpha",
                            claimAtUtc: now.AddMinutes(-1),
                            claimExpiresAtUtc: now.AddMinutes(1))],
                        "accepted-page-2")),
                (WorkflowInstallationStatus.Accepted, "accepted-page-2") => Task.FromResult(
                    new WorkflowDeliveryListResult(
                        [WorkflowDeliveryProvisioningExecutorTests.Delivery(
                            WorkflowInstallationStatus.Accepted,
                            pageSuffix: "beta",
                            claimantId: "worker-alpha",
                            claimAtUtc: now.AddMinutes(-1),
                            claimExpiresAtUtc: now.AddMinutes(1))],
                        null)),
                (WorkflowInstallationStatus.ProvisioningAccepted, null) => Task.FromResult(
                    new WorkflowDeliveryListResult(
                        [WorkflowDeliveryProvisioningExecutorTests.Delivery(
                            WorkflowInstallationStatus.ProvisioningAccepted,
                            pageSuffix: "gamma",
                            claimantId: "worker-alpha",
                            claimAtUtc: now.AddMinutes(-1),
                            claimExpiresAtUtc: now.AddMinutes(1))],
                        null)),
                _ => throw new InvalidOperationException("Unexpected continuation page."),
            };
        }

        public Task<WorkflowDeliverySnapshot?> GetAsync(string deliveryId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliverySnapshot?> GetForScopeAsync(string deliveryId, string targetScopeId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliverySnapshot?> FindByInstallationAsync(string scopeId, string installationId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingProvisioningExecutor : IWorkflowDeliveryProvisioningExecutor
    {
        public List<WorkflowDeliverySnapshot> Deliveries { get; } = [];

        public Task<WorkflowDeliveryProvisioningExecutionResult> ExecuteAsync(
            WorkflowDeliverySnapshot delivery,
            string continuationClaimantId,
            CancellationToken ct = default)
        {
            Deliveries.Add(delivery);
            return Task.FromResult(new WorkflowDeliveryProvisioningExecutionResult(
                WorkflowDeliveryProvisioningExecutionStatus.ProvisioningAccepted,
                delivery.Installation!.InstallationId,
                delivery.Installation.Attempt,
                delivery.Installation.OperationId));
        }
    }

    private sealed class FailingFirstProvisioningExecutor(string failingDeliveryId)
        : IWorkflowDeliveryProvisioningExecutor
    {
        public List<string> Attempts { get; } = [];
        public List<string> Succeeded { get; } = [];

        public Task<WorkflowDeliveryProvisioningExecutionResult> ExecuteAsync(
            WorkflowDeliverySnapshot delivery,
            string continuationClaimantId,
            CancellationToken ct = default)
        {
            Attempts.Add(delivery.DeliveryId);
            if (string.Equals(delivery.DeliveryId, failingDeliveryId, StringComparison.Ordinal))
                throw new InvalidOperationException("Injected continuation failure.");
            Succeeded.Add(delivery.DeliveryId);
            return Task.FromResult(new WorkflowDeliveryProvisioningExecutionResult(
                WorkflowDeliveryProvisioningExecutionStatus.ProvisioningAccepted,
                delivery.Installation!.InstallationId,
                delivery.Installation.Attempt,
                delivery.Installation.OperationId));
        }
    }

    private sealed class DeadlineBlockingProvisioningExecutor(string blockingDeliveryId)
        : IWorkflowDeliveryProvisioningExecutor
    {
        public TaskCompletionSource<bool> BlockingAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Attempts { get; } = [];
        public List<string> Succeeded { get; } = [];
        public bool CancellationObserved { get; private set; }

        public async Task<WorkflowDeliveryProvisioningExecutionResult> ExecuteAsync(
            WorkflowDeliverySnapshot delivery,
            string continuationClaimantId,
            CancellationToken ct = default)
        {
            Attempts.Add(delivery.DeliveryId);
            if (string.Equals(delivery.DeliveryId, blockingDeliveryId, StringComparison.Ordinal))
            {
                BlockingAttemptStarted.TrySetResult(true);
                var blocked = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = ct.Register(() => blocked.TrySetCanceled(ct));
                try
                {
                    await blocked.Task;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    CancellationObserved = true;
                    throw;
                }
            }

            Succeeded.Add(delivery.DeliveryId);
            return new WorkflowDeliveryProvisioningExecutionResult(
                WorkflowDeliveryProvisioningExecutionStatus.ProvisioningAccepted,
                delivery.Installation!.InstallationId,
                delivery.Installation.Attempt,
                delivery.Installation.OperationId);
        }
    }

    private sealed class AcceptedDeliveriesQueryPort(IReadOnlyList<WorkflowDeliverySnapshot> deliveries)
        : IWorkflowDeliveryQueryPort
    {
        public Task<WorkflowDeliveryListResult> ListAsync(
            WorkflowDeliveryListQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(new WorkflowDeliveryListResult(
                query.InstallationStatus == WorkflowInstallationStatus.Accepted
                    ? deliveries
                    : [],
                null));

        public Task<WorkflowDeliverySnapshot?> GetAsync(string deliveryId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliverySnapshot?> GetForScopeAsync(string deliveryId, string targetScopeId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliverySnapshot?> FindByInstallationAsync(string scopeId, string installationId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    [Theory]
    [InlineData(WorkflowInstallationStatus.Accepted, true, false)]
    [InlineData(WorkflowInstallationStatus.Accepted, false, true)]
    [InlineData(WorkflowInstallationStatus.ProvisioningAccepted, true, false)]
    [InlineData(WorkflowInstallationStatus.ProvisioningAccepted, false, true)]
    public async Task ScanOnceAsync_WhenDeliveryWasWithdrawnBeforeClaim_ShouldDispatchActorOwnedReconciliation(
        WorkflowInstallationStatus status,
        bool revoked,
        bool expired)
    {
        var now = DateTimeOffset.Parse("2026-08-16T06:00:00Z");
        var delivery = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            status,
            pageSuffix: $"withdrawn-{status}",
            includeContinuationClaim: false);
        if (revoked)
        {
            delivery = delivery with
            {
                LifecycleStatus = WorkflowDeliveryLifecycleStatus.Revoked,
                RevokedBy = "admin-alpha",
                RevokedAtUtc = now,
            };
        }

        if (expired)
            delivery = delivery with { ExpiresAtUtc = now.AddMinutes(-1) };

        var materializer = new RecordingArtifactMaterializer();
        var readiness = new RecordingReadinessReconciler();
        var provisioning = new RecordingProvisioningExecutor();
        var commands = new RecordingCommandPort();
        var scanner = new WorkflowDeliveryContinuationScanner(
            new SingleDeliveryQueryPort(delivery),
            provisioning,
            materializer,
            readiness,
            commands,
            new FixedTimeProvider(now),
            NullLogger<WorkflowDeliveryContinuationScanner>.Instance,
            WorkerOptions());

        await scanner.ScanOnceAsync();

        provisioning.Deliveries.Should().BeEmpty();
        materializer.Deliveries.Should().BeEmpty();
        readiness.Deliveries.Should().BeEmpty();
        commands.Claims.Should().ContainSingle();
        commands.Failures.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkflowInstallationStatus.Accepted)]
    [InlineData(WorkflowInstallationStatus.ProvisioningAccepted)]
    public async Task ScanOnceAsync_WhenClaimCommittedBeforeRevoke_ShouldFinishOwnedContinuationWithinLease(
        WorkflowInstallationStatus status)
    {
        var now = DateTimeOffset.Parse("2026-08-16T06:00:00Z");
        var delivery = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            status,
            pageSuffix: $"claimed-before-revoke-{status}",
            claimantId: "worker-alpha",
            claimAtUtc: now.AddMinutes(-1),
            claimExpiresAtUtc: now.AddMinutes(1)) with
        {
            LifecycleStatus = WorkflowDeliveryLifecycleStatus.Revoked,
            RevokedBy = "admin-alpha",
            RevokedAtUtc = now,
        };
        var provisioning = new RecordingProvisioningExecutor();
        var materializer = new RecordingArtifactMaterializer();
        var readiness = new RecordingReadinessReconciler();
        var commands = new RecordingCommandPort();
        var scanner = new WorkflowDeliveryContinuationScanner(
            new SingleDeliveryQueryPort(delivery),
            provisioning,
            materializer,
            readiness,
            commands,
            new FixedTimeProvider(now),
            NullLogger<WorkflowDeliveryContinuationScanner>.Instance,
            WorkerOptions());

        await scanner.ScanOnceAsync();

        commands.Claims.Should().BeEmpty();
        if (status == WorkflowInstallationStatus.Accepted)
        {
            provisioning.Deliveries.Should().ContainSingle();
            materializer.Deliveries.Should().BeEmpty();
            readiness.Deliveries.Should().BeEmpty();
        }
        else
        {
            provisioning.Deliveries.Should().BeEmpty();
            materializer.Deliveries.Should().ContainSingle();
            readiness.Deliveries.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task ScanOnceAsync_WhenDeliveryIsActiveAndUnexpired_ShouldStillResumeContinuation()
    {
        var delivery = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.ProvisioningAccepted,
            pageSuffix: "active",
            claimantId: "worker-alpha",
            claimAtUtc: DateTimeOffset.Parse("2026-08-16T05:59:00Z"),
            claimExpiresAtUtc: DateTimeOffset.Parse("2026-08-16T06:01:00Z"));
        var materializer = new RecordingArtifactMaterializer();
        var readiness = new RecordingReadinessReconciler();
        var scanner = new WorkflowDeliveryContinuationScanner(
            new SingleDeliveryQueryPort(delivery),
            new RecordingProvisioningExecutor(),
            materializer,
            readiness,
            new RecordingCommandPort(),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T06:00:00Z")),
            NullLogger<WorkflowDeliveryContinuationScanner>.Instance,
            WorkerOptions());

        await scanner.ScanOnceAsync();

        readiness.Deliveries.Select(static item => item.DeliveryId).Should().Equal("delivery-active");
    }

    private sealed class SingleDeliveryQueryPort(WorkflowDeliverySnapshot delivery) : IWorkflowDeliveryQueryPort
    {
        public Task<WorkflowDeliveryListResult> ListAsync(
            WorkflowDeliveryListQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(new WorkflowDeliveryListResult(
                query.InstallationStatus == delivery.Installation?.Status
                    ? [delivery]
                    : [],
                null));

        public Task<WorkflowDeliverySnapshot?> GetAsync(string deliveryId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliverySnapshot?> GetForScopeAsync(string deliveryId, string targetScopeId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliverySnapshot?> FindByInstallationAsync(string scopeId, string installationId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class MutableSingleDeliveryQueryPort(WorkflowDeliverySnapshot delivery)
        : IWorkflowDeliveryQueryPort
    {
        public WorkflowDeliverySnapshot Delivery { get; set; } = delivery;

        public Task<WorkflowDeliveryListResult> ListAsync(
            WorkflowDeliveryListQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(new WorkflowDeliveryListResult(
                query.InstallationStatus == Delivery.Installation?.Status
                    ? [Delivery]
                    : [],
                null));

        public Task<WorkflowDeliverySnapshot?> GetAsync(string deliveryId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliverySnapshot?> GetForScopeAsync(string deliveryId, string targetScopeId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkflowDeliverySnapshot?> FindByInstallationAsync(string scopeId, string installationId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingReadinessReconciler(
        WorkflowInstallationReadinessReconciliationResult? result = null,
        List<string>? calls = null)
        : IWorkflowInstallationReadinessReconciler
    {
        public List<WorkflowDeliverySnapshot> Deliveries { get; } = [];

        public Task<WorkflowInstallationReadinessReconciliationResult> ReconcileAsync(
            WorkflowDeliverySnapshot delivery,
            string continuationClaimantId,
            CancellationToken ct = default)
        {
            Deliveries.Add(delivery);
            calls?.Add($"readiness:{delivery.DeliveryId}");
            return Task.FromResult(result ?? new WorkflowInstallationReadinessReconciliationResult(
                WorkflowInstallationReadinessReconciliationStatus.Pending,
                "pending",
                "Pending read-model evidence."));
        }
    }

    private sealed class RecordingArtifactMaterializer(
        WorkflowAcceptanceArtifactMaterializationResult? result = null,
        List<string>? calls = null)
        : IWorkflowAcceptanceArtifactMaterializer
    {
        public List<WorkflowDeliverySnapshot> Deliveries { get; } = [];

        public Task<WorkflowAcceptanceArtifactMaterializationResult> MaterializeAsync(
            WorkflowDeliverySnapshot delivery,
            string continuationClaimantId,
            CancellationToken ct = default)
        {
            Deliveries.Add(delivery);
            calls?.Add($"materialize:{delivery.DeliveryId}");
            return Task.FromResult(result ?? new WorkflowAcceptanceArtifactMaterializationResult(
                WorkflowAcceptanceArtifactMaterializationStatus.Satisfied,
                "satisfied",
                "Acceptance artifact is satisfied."));
        }
    }

    private sealed class RecordingCommandPort : IWorkflowDeliveryCommandPort
    {
        public string? FailingClaimDeliveryId { get; init; }

        public List<ClaimWorkflowInstallationContinuationMutation> Claims { get; } = [];

        public List<RecordWorkflowInstallationFailedMutation> Failures { get; } = [];

        public Task<WorkflowDeliveryCommandReceipt> ClaimInstallationContinuationAsync(
            ClaimWorkflowInstallationContinuationMutation mutation,
            CancellationToken ct = default)
        {
            Claims.Add(mutation);
            if (string.Equals(mutation.DeliveryId, FailingClaimDeliveryId, StringComparison.Ordinal))
                throw new InvalidOperationException("Injected claim dispatch failure.");
            return Accepted(mutation.DeliveryId);
        }

        public Task<WorkflowDeliveryCommandReceipt> RecordInstallationFailedAsync(
            RecordWorkflowInstallationFailedMutation mutation,
            CancellationToken ct = default)
        {
            Failures.Add(mutation);
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
        public Task<WorkflowDeliveryCommandReceipt> RecordProvisioningAcceptedAsync(RecordWorkflowProvisioningAcceptedMutation mutation, CancellationToken ct = default) =>
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

    private static IOptions<WorkflowDeliveryContinuationWorkerOptions> WorkerOptions(int pageSize = 10) =>
        Options.Create(new WorkflowDeliveryContinuationWorkerOptions
        {
            PageSize = pageSize,
            ClaimantId = "worker-alpha",
            ClaimDurationSeconds = 120,
        });

    private sealed record QueryCall(
        WorkflowInstallationStatus? Status,
        string? Cursor);
}
