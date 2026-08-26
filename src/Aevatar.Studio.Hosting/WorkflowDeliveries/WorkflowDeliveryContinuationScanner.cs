using Aevatar.Studio.Application.Delivery;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Hosting.WorkflowDeliveries;

public sealed class WorkflowDeliveryContinuationScanner
{
    private static readonly TimeSpan ExecutionDeadlineSafetyMargin = TimeSpan.FromSeconds(1);

    private readonly IWorkflowDeliveryQueryPort _queries;
    private readonly IWorkflowDeliveryProvisioningExecutor _provisioning;
    private readonly IWorkflowAcceptanceArtifactMaterializer _artifactMaterializer;
    private readonly IWorkflowInstallationReadinessReconciler _readiness;
    private readonly IWorkflowDeliveryCommandPort _commands;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowDeliveryContinuationScanner> _logger;
    private readonly int _pageSize;
    private readonly TimeSpan _claimDuration;
    private readonly string _claimantId;

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
        _claimDuration = options.Value.ClaimDuration;
        _claimantId = string.IsNullOrWhiteSpace(options.Value.ClaimantId)
            ? throw new InvalidOperationException("Workflow delivery continuation claimant identity is required.")
            : options.Value.ClaimantId.Trim();
    }

    public async Task ScanOnceAsync(CancellationToken ct = default)
    {
        await ScanStatusAsync(
            WorkflowInstallationStatus.Accepted,
            async (delivery, cancellationToken) =>
            {
                _ = await _provisioning.ExecuteAsync(delivery, _claimantId, cancellationToken);
            },
            ct);
        await ScanStatusAsync(
            WorkflowInstallationStatus.ProvisioningAccepted,
            async (delivery, cancellationToken) =>
            {
                var materialization = await _artifactMaterializer.MaterializeAsync(
                    delivery,
                    _claimantId,
                    cancellationToken);
                if (materialization.Status == WorkflowAcceptanceArtifactMaterializationStatus.TerminalFailure)
                {
                    await RecordFailureAsync(
                        delivery,
                        materialization.Code,
                        materialization.Message,
                        cancellationToken);
                    return;
                }
                var result = await _readiness.ReconcileAsync(delivery, _claimantId, cancellationToken);
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
        var claim = installation?.ContinuationClaim;
        if (installation == null || claim == null)
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
                _timeProvider.GetUtcNow(),
                claim.ClaimId,
                claim.ClaimantId),
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
                try
                {
                    await ContinueDeliveryAsync(delivery, status, continueAsync, ct);
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

    private async Task ContinueDeliveryAsync(
        WorkflowDeliverySnapshot delivery,
        WorkflowInstallationStatus status,
        Func<WorkflowDeliverySnapshot, CancellationToken, Task> continueAsync,
        CancellationToken ct)
    {
        var installation = delivery.Installation;
        if (installation == null)
            return;
        var now = _timeProvider.GetUtcNow();
        if (delivery.LifecycleStatus != WorkflowDeliveryLifecycleStatus.Active ||
            delivery.ExpiresAtUtc <= now)
        {
            await ClaimAsync(delivery, status, ct);
            return;
        }

        var claim = installation.ContinuationClaim;
        if (!ClaimMatchesActiveStage(claim, installation, status) ||
            claim!.ExpiresAtUtc <= now)
        {
            await ClaimAsync(delivery, status, ct);
            return;
        }
        if (!string.Equals(claim.ClaimantId, _claimantId, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "Skipping workflow delivery continuation for delivery {DeliveryId}: active claim {ClaimId} belongs to another worker.",
                delivery.DeliveryId,
                claim.ClaimId);
            return;
        }

        var deadlineAtUtc = claim.ExpiresAtUtc.Subtract(ExecutionDeadlineSafetyMargin);
        var deadlineDelay = deadlineAtUtc - _timeProvider.GetUtcNow();
        if (deadlineDelay <= TimeSpan.Zero)
        {
            _logger.LogDebug(
                "Skipping workflow delivery continuation for delivery {DeliveryId}: claim {ClaimId} has no safe execution budget remaining.",
                delivery.DeliveryId,
                claim.ClaimId);
            return;
        }

        using var deadline = new CancellationTokenSource(deadlineDelay, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            await continueAsync(delivery, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Workflow delivery continuation for delivery {DeliveryId} reached claim {ClaimId} execution deadline.",
                delivery.DeliveryId,
                claim.ClaimId);
        }
    }

    private async Task ClaimAsync(
        WorkflowDeliverySnapshot delivery,
        WorkflowInstallationStatus status,
        CancellationToken ct)
    {
        var installation = delivery.Installation;
        if (installation == null)
            return;
        await _commands.ClaimInstallationContinuationAsync(
            new ClaimWorkflowInstallationContinuationMutation(
                delivery.DeliveryId,
                installation.InstallationId,
                status,
                installation.Attempt,
                installation.OperationId,
                $"claim-{Guid.NewGuid():N}",
                _claimantId,
                _claimDuration),
            ct);
    }

    private static bool ClaimMatchesActiveStage(
        WorkflowInstallationContinuationClaimSnapshot? claim,
        WorkflowInstallationSnapshot installation,
        WorkflowInstallationStatus status) =>
        claim != null &&
        claim.ExpectedStatus == status &&
        claim.Attempt == installation.Attempt &&
        string.Equals(claim.OperationId, installation.OperationId, StringComparison.Ordinal);

    private static string? NormalizeCursor(string? cursor) =>
        string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim();
}
