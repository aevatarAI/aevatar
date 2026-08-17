using Aevatar.Studio.Application.Delivery;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Hosting.WorkflowDeliveries;

public sealed class WorkflowDeliveryContinuationScanner
{
    private readonly IWorkflowDeliveryQueryPort _queries;
    private readonly IWorkflowDeliveryProvisioningExecutor _provisioning;
    private readonly IWorkflowAcceptanceArtifactMaterializer _artifactMaterializer;
    private readonly IWorkflowInstallationReadinessReconciler _readiness;
    private readonly IWorkflowDeliveryCommandPort _commands;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowDeliveryContinuationScanner> _logger;
    private readonly int _pageSize;

    public WorkflowDeliveryContinuationScanner(
        IWorkflowDeliveryQueryPort queries,
        IWorkflowDeliveryProvisioningExecutor provisioning,
        IWorkflowAcceptanceArtifactMaterializer artifactMaterializer,
        IWorkflowInstallationReadinessReconciler readiness,
        IWorkflowDeliveryCommandPort commands,
        TimeProvider timeProvider,
        ILogger<WorkflowDeliveryContinuationScanner> logger,
        IOptions<WorkflowDeliveryContinuationWorkerOptions> options)
    {
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _provisioning = provisioning ?? throw new ArgumentNullException(nameof(provisioning));
        _artifactMaterializer = artifactMaterializer ?? throw new ArgumentNullException(nameof(artifactMaterializer));
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(options);
        _pageSize = Math.Clamp(options.Value.PageSize, 1, 200);
    }

    public async Task ScanOnceAsync(CancellationToken ct = default)
    {
        await ScanStatusAsync(
            WorkflowInstallationStatus.Accepted,
            async (delivery, cancellationToken) =>
            {
                _ = await _provisioning.ExecuteAsync(delivery, cancellationToken);
            },
            ct);
        await ScanStatusAsync(
            WorkflowInstallationStatus.ProvisioningAccepted,
            async (delivery, cancellationToken) =>
            {
                var materialization = await _artifactMaterializer.MaterializeAsync(delivery, cancellationToken);
                if (materialization.Status == WorkflowAcceptanceArtifactMaterializationStatus.TerminalFailure)
                {
                    await RecordFailureAsync(
                        delivery,
                        materialization.Code,
                        materialization.Message,
                        cancellationToken);
                    return;
                }
                var result = await _readiness.ReconcileAsync(delivery, cancellationToken);
                if (result.Status != WorkflowInstallationReadinessReconciliationStatus.TerminalFailure)
                    return;
                await RecordFailureAsync(delivery, result.Code, result.Message, cancellationToken);
            },
            ct);
    }

    private async Task RecordFailureAsync(
        WorkflowDeliverySnapshot delivery,
        string code,
        string message,
        CancellationToken ct)
    {
        var installation = delivery.Installation;
        if (installation == null)
            return;
        await _commands.RecordInstallationFailedAsync(
            new RecordWorkflowInstallationFailedMutation(
                delivery.DeliveryId,
                installation.InstallationId,
                code,
                message,
                WorkflowInstallationStatus.ProvisioningAccepted,
                installation.Attempt,
                installation.OperationId,
                _timeProvider.GetUtcNow()),
            ct);
    }

    private async Task ScanStatusAsync(
        WorkflowInstallationStatus status,
        Func<WorkflowDeliverySnapshot, CancellationToken, Task> continueAsync,
        CancellationToken ct)
    {
        string? cursor = null;
        do
        {
            var page = await _queries.ListAsync(
                new WorkflowDeliveryListQuery(
                    PageSize: _pageSize,
                    PageToken: cursor,
                    InstallationStatus: status),
                ct);
            foreach (var delivery in page.Items)
            {
                if (delivery.Installation?.Status != status)
                    continue;
                // Revoking or expiring a delivery withdraws the administrator's authority to
                // keep provisioning into the customer's scope. The actor guards the customer's
                // own mutations, but every continuation here runs off the read model with no
                // request to reject, so the withdrawal has to be honoured before resuming.
                if (!IsDeliveryAvailable(delivery))
                {
                    _logger.LogInformation(
                        "Skipping workflow delivery continuation for delivery {DeliveryId}: the delivery is no longer available.",
                        delivery.DeliveryId);
                    continue;
                }
                try
                {
                    await continueAsync(delivery, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Workflow delivery continuation failed for delivery {DeliveryId} in installation status {InstallationStatus}.",
                        delivery.DeliveryId,
                        status);
                }
            }

            var nextCursor = NormalizeCursor(page.NextPageToken);
            if (nextCursor != null && string.Equals(nextCursor, cursor, StringComparison.Ordinal))
                throw new InvalidOperationException("Workflow delivery continuation query returned a non-advancing cursor.");
            cursor = nextCursor;
        }
        while (cursor != null);
    }

    private bool IsDeliveryAvailable(WorkflowDeliverySnapshot delivery) =>
        delivery.LifecycleStatus != WorkflowDeliveryLifecycleStatus.Revoked &&
        delivery.ExpiresAtUtc > _timeProvider.GetUtcNow();

    private static string? NormalizeCursor(string? cursor) =>
        string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim();
}
