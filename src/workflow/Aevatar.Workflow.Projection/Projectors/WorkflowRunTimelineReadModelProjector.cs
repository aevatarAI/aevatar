using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowRunTimelineReadModelProjector
    : IProjectionProjector<WorkflowRunInsightProjectionContext, bool>
{
    private readonly IProjectionWriteDispatcher<WorkflowRunTimelineDocument> _writeDispatcher;

    public WorkflowRunTimelineReadModelProjector(
        IProjectionWriteDispatcher<WorkflowRunTimelineDocument> writeDispatcher)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
    }

    public ValueTask InitializeAsync(WorkflowRunInsightProjectionContext context, CancellationToken ct = default)
    {
        _ = context;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(
        WorkflowRunInsightProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        _ = context;

        if (!WorkflowRunInsightProjectionMaps.TryUnpack(envelope, out var stateEvent, out var state))
            return;

        await _writeDispatcher.UpsertAsync(
            WorkflowRunInsightProjectionMaps.ToTimelineDocument(state!, stateEvent!),
            ct);
    }

    public ValueTask CompleteAsync(
        WorkflowRunInsightProjectionContext context,
        bool completion,
        CancellationToken ct = default)
    {
        _ = context;
        _ = completion;
        _ = ct;
        return ValueTask.CompletedTask;
    }
}
