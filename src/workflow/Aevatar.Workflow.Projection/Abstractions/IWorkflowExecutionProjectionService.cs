using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.CQRS.Projection.Abstractions;

namespace Aevatar.Workflow.Projection;

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

    Task<ProjectionRunCompletionStatus> WaitForRunProjectionCompletionStatusAsync(
        string runId,
        TimeSpan? timeoutOverride = null,
        CancellationToken ct = default);

    Task<WorkflowExecutionReport?> CompleteAsync(
        WorkflowExecutionProjectionSession session,
        IReadOnlyList<WorkflowExecutionTopologyEdge> topology,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowExecutionReport>> ListRunsAsync(int take = 50, CancellationToken ct = default);

    Task<WorkflowExecutionReport?> GetRunAsync(string runId, CancellationToken ct = default);
}
