using Aevatar.Studio.Application.Delivery;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting.WorkflowDeliveries;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowDeliveryContinuationScannerTests
{
    [Fact]
    public async Task ScanOnceAsync_AfterRestart_ShouldPageAcceptedAndInvokeReadinessReconciliation()
    {
        var queries = new PagedQueryPort();
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
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T06:00:00Z")),
            NullLogger<WorkflowDeliveryContinuationScanner>.Instance,
            Options.Create(new WorkflowDeliveryContinuationWorkerOptions { PageSize = 1 }));

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

    [Fact]
    public async Task ScanOnceAsync_WhenReadinessIsTerminalFailure_ShouldPersistFencedInstallationFailure()
    {
        var delivery = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.ProvisioningAccepted,
            pageSuffix: "terminal");
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
            Options.Create(new WorkflowDeliveryContinuationWorkerOptions { PageSize = 10 }));

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
    }

    [Fact]
    public async Task ScanOnceAsync_WhenArtifactMaterializationIsTerminalFailure_ShouldPersistFencedFailureWithoutReadiness()
    {
        var delivery = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.ProvisioningAccepted,
            pageSuffix: "artifact-terminal");
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
            Options.Create(new WorkflowDeliveryContinuationWorkerOptions { PageSize = 10 }));

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
    }

    [Fact]
    public async Task ScanOnceAsync_WhenOneContinuationFails_ShouldContinueWithLaterDeliveries()
    {
        var first = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.Accepted,
            pageSuffix: "fails");
        var second = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.Accepted,
            pageSuffix: "continues");
        var provisioning = new FailingFirstProvisioningExecutor(first.DeliveryId);
        var scanner = new WorkflowDeliveryContinuationScanner(
            new AcceptedDeliveriesQueryPort([first, second]),
            provisioning,
            new RecordingArtifactMaterializer(),
            new RecordingReadinessReconciler(),
            new RecordingCommandPort(),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T07:00:00Z")),
            NullLogger<WorkflowDeliveryContinuationScanner>.Instance,
            Options.Create(new WorkflowDeliveryContinuationWorkerOptions { PageSize = 10 }));

        await scanner.ScanOnceAsync();

        provisioning.Attempts.Should().Equal(first.DeliveryId, second.DeliveryId);
        provisioning.Succeeded.Should().Equal(second.DeliveryId);
    }

    private sealed class PagedQueryPort : IWorkflowDeliveryQueryPort
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
                            pageSuffix: "alpha")],
                        "accepted-page-2")),
                (WorkflowInstallationStatus.Accepted, "accepted-page-2") => Task.FromResult(
                    new WorkflowDeliveryListResult(
                        [WorkflowDeliveryProvisioningExecutorTests.Delivery(
                            WorkflowInstallationStatus.Accepted,
                            pageSuffix: "beta")],
                        null)),
                (WorkflowInstallationStatus.ProvisioningAccepted, null) => Task.FromResult(
                    new WorkflowDeliveryListResult(
                        [WorkflowDeliveryProvisioningExecutorTests.Delivery(
                            WorkflowInstallationStatus.ProvisioningAccepted,
                            pageSuffix: "gamma")],
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

    private sealed class SingleDeliveryQueryPort(WorkflowDeliverySnapshot delivery) : IWorkflowDeliveryQueryPort
    {
        public Task<WorkflowDeliveryListResult> ListAsync(
            WorkflowDeliveryListQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(new WorkflowDeliveryListResult(
                query.InstallationStatus == WorkflowInstallationStatus.ProvisioningAccepted
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

    private sealed class RecordingReadinessReconciler(
        WorkflowInstallationReadinessReconciliationResult? result = null,
        List<string>? calls = null)
        : IWorkflowInstallationReadinessReconciler
    {
        public List<WorkflowDeliverySnapshot> Deliveries { get; } = [];

        public Task<WorkflowInstallationReadinessReconciliationResult> ReconcileAsync(
            WorkflowDeliverySnapshot delivery,
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
        public List<RecordWorkflowInstallationFailedMutation> Failures { get; } = [];

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

    private sealed record QueryCall(
        WorkflowInstallationStatus? Status,
        string? Cursor);
}
