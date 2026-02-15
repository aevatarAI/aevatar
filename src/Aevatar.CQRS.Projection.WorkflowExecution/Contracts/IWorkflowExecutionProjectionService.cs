using Aevatar.CQRS.Projection.WorkflowExecution.ReadModels;

namespace Aevatar.CQRS.Projection.WorkflowExecution;

/// <summary>
/// Application-facing facade for chat run projection lifecycle.
/// </summary>
public interface IWorkflowExecutionProjectionService
{
    bool ProjectionEnabled { get; }

    bool EnableRunQueryEndpoints { get; }

    bool EnableRunReportArtifacts { get; }

    Task<WorkflowExecutionProjectionSession> StartAsync(
        string rootActorId,
        string workflowName,
        string input,
        CancellationToken ct = default);

    Task ProjectAsync(
        WorkflowExecutionProjectionSession session,
        EventEnvelope envelope,
        CancellationToken ct = default);

    Task<bool> WaitForRunProjectionCompletedAsync(string runId, CancellationToken ct = default);

    Task<WorkflowExecutionReport?> CompleteAsync(
        WorkflowExecutionProjectionSession session,
        IReadOnlyList<WorkflowExecutionTopologyEdge> topology,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowExecutionReport>> ListRunsAsync(int take = 50, CancellationToken ct = default);

    Task<WorkflowExecutionReport?> GetRunAsync(string runId, CancellationToken ct = default);
}
