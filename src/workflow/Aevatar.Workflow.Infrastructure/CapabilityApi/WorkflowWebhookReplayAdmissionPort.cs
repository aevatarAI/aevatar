using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal sealed class WorkflowWebhookReplayAdmissionPort(
    IWorkflowWebhookReplayStore? replayStore = null) : IWorkflowWebhookReplayAdmissionPort
{
    public bool IsAvailable => replayStore != null;

    public ValueTask<WorkflowWebhookReplayAdmission> AdmitAsync(
        WorkflowWebhookReplayAdmissionRequest request,
        CancellationToken ct = default)
    {
        if (replayStore == null)
            throw new InvalidOperationException("Workflow webhook replay store is unavailable.");

        return replayStore.AdmitAsync(request, ct);
    }

    public ValueTask ReleaseAsync(
        WorkflowWebhookReplayAdmissionRequest request,
        CancellationToken ct = default)
    {
        if (replayStore == null)
            return ValueTask.CompletedTask;

        return replayStore.ReleaseAsync(request, ct);
    }
}
