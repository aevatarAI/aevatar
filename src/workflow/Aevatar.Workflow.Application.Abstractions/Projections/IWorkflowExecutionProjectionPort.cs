using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Abstractions.Projections;

public interface IWorkflowExecutionProjectionPort
{
    bool ProjectionEnabled { get; }

    bool EnableRunQueryEndpoints { get; }

    Task<WorkflowProjectionSession> StartAsync(
        string rootActorId,
        string workflowName,
        string input,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default);

    Task<WorkflowProjectionCompletionStatus> WaitForRunProjectionCompletionStatusAsync(
        string runId,
        TimeSpan? timeoutOverride = null,
        CancellationToken ct = default);

    Task<WorkflowRunReport?> CompleteAsync(
        WorkflowProjectionSession session,
        IReadOnlyList<WorkflowRunTopologyEdge> topology,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowRunSummary>> ListRunsAsync(
        int take = 50,
        CancellationToken ct = default);

    Task<WorkflowRunReport?> GetRunAsync(
        string runId,
        CancellationToken ct = default);
}

public sealed class WorkflowProjectionSession
{
    public required string RunId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required bool Enabled { get; init; }
}
