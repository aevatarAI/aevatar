using System.Collections.Concurrent;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public sealed class InMemoryWorkflowWebhookReplayStore : IWorkflowWebhookReplayStore
{
    private readonly ConcurrentDictionary<string, WorkflowWebhookReplayAdmissionRequest> _admitted = new(StringComparer.Ordinal);

    public ValueTask<WorkflowWebhookReplayAdmission> AdmitAsync(
        WorkflowWebhookReplayAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var key = BuildKey(request.RouteKey, request.SourceId, request.DeliveryId);
        var existing = _admitted.GetOrAdd(key, request);
        if (ReferenceEquals(existing, request))
            return ValueTask.FromResult(new WorkflowWebhookReplayAdmission(
                WorkflowWebhookReplayAdmissionStatus.Admitted,
                request.CommandId,
                request.CorrelationId));

        var status = string.Equals(existing.PayloadFingerprint, request.PayloadFingerprint, StringComparison.Ordinal)
            ? WorkflowWebhookReplayAdmissionStatus.DuplicateInProgress
            : WorkflowWebhookReplayAdmissionStatus.PayloadConflict;
        return ValueTask.FromResult(new WorkflowWebhookReplayAdmission(
            status,
            existing.CommandId,
            existing.CorrelationId));
    }

    public ValueTask ReleaseAsync(
        WorkflowWebhookReplayAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var key = BuildKey(request.RouteKey, request.SourceId, request.DeliveryId);
        _admitted.TryRemove(
            new KeyValuePair<string, WorkflowWebhookReplayAdmissionRequest>(key, request));
        return ValueTask.CompletedTask;
    }

    private static string BuildKey(string routeKey, string sourceId, string deliveryId) =>
        $"{routeKey}\n{sourceId}\n{deliveryId}";
}
