using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Orchestration;

public interface IWorkflowRunInsightActorPort
{
    Task EnsureActorAsync(string rootActorId, CancellationToken ct = default);

    Task PublishObservedAsync(
        string rootActorId,
        WorkflowRunInsightObservedEvent evt,
        CancellationToken ct = default);

    Task CaptureTopologyAsync(
        string rootActorId,
        string workflowName,
        string commandId,
        IReadOnlyList<ReadModels.WorkflowExecutionTopologyEdge> topology,
        DateTimeOffset capturedAt,
        CancellationToken ct = default);

    Task MarkStoppedAsync(
        string rootActorId,
        string reason,
        DateTimeOffset stoppedAt,
        CancellationToken ct = default);
}
