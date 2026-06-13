using System.Collections.Concurrent;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public sealed class InMemoryWorkflowWebhookReplayStore : IWorkflowWebhookReplayStore
{
    private readonly ConcurrentDictionary<string, InMemoryWorkflowWebhookReplayRecord> _admitted = new(StringComparer.Ordinal);

    public ValueTask<WorkflowWebhookReplayAdmission> AdmitAsync(
        WorkflowWebhookReplayAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var key = BuildKey(request.RouteKey, request.SourceId, request.DeliveryId);
        var record = new InMemoryWorkflowWebhookReplayRecord(request, Completed: false);
        var existing = _admitted.GetOrAdd(key, record);
        if (ReferenceEquals(existing, record))
            return ValueTask.FromResult(new WorkflowWebhookReplayAdmission(
                WorkflowWebhookReplayAdmissionStatus.Admitted,
                request.CommandId,
                request.CorrelationId));

        var status = string.Equals(existing.Request.PayloadFingerprint, request.PayloadFingerprint, StringComparison.Ordinal)
            ? existing.Completed
                ? WorkflowWebhookReplayAdmissionStatus.DuplicateCompleted
                : WorkflowWebhookReplayAdmissionStatus.DuplicateInProgress
            : WorkflowWebhookReplayAdmissionStatus.PayloadConflict;
        return ValueTask.FromResult(new WorkflowWebhookReplayAdmission(
            status,
            existing.Request.CommandId,
            existing.Request.CorrelationId));
    }

    public ValueTask CompleteAsync(
        WorkflowWebhookReplayAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var key = BuildKey(request.RouteKey, request.SourceId, request.DeliveryId);
        if (_admitted.TryGetValue(key, out var existing) && existing.Request == request)
            _admitted.TryUpdate(key, existing with { Completed = true }, existing);

        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAsync(
        WorkflowWebhookReplayAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var key = BuildKey(request.RouteKey, request.SourceId, request.DeliveryId);
        _admitted.TryRemove(
            new KeyValuePair<string, InMemoryWorkflowWebhookReplayRecord>(
                key,
                new InMemoryWorkflowWebhookReplayRecord(request, Completed: false)));
        return ValueTask.CompletedTask;
    }

    private static string BuildKey(string routeKey, string sourceId, string deliveryId) =>
        $"{routeKey}\n{sourceId}\n{deliveryId}";

    private sealed record InMemoryWorkflowWebhookReplayRecord(
        WorkflowWebhookReplayAdmissionRequest Request,
        bool Completed);
}
