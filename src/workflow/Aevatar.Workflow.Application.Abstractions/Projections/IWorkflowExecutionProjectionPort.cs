using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Abstractions.Projections;

public interface IWorkflowExecutionProjectionPort
{
    bool ProjectionEnabled { get; }

    bool EnableActorQueryEndpoints { get; }

    Task EnsureActorProjectionAsync(
        string rootActorId,
        string workflowName,
        string input,
        string commandId,
        CancellationToken ct = default);

    Task AttachLiveSinkAsync(
        string actorId,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default);

    Task DetachLiveSinkAsync(
        string actorId,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default);

    Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(
        string actorId,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(
        string actorId,
        int take = 200,
        CancellationToken ct = default);
}
