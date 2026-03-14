using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Reducers;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionReadModelUpdater : IWorkflowProjectionReadModelUpdater
{
    private readonly IProjectionWriteDispatcher<WorkflowExecutionReport, string> _writeDispatcher;
    private readonly IProjectionDocumentReader<WorkflowExecutionReport, string> _documentReader;
    private readonly IProjectionClock _clock;

    public WorkflowProjectionReadModelUpdater(
        IProjectionWriteDispatcher<WorkflowExecutionReport, string> writeDispatcher,
        IProjectionDocumentReader<WorkflowExecutionReport, string> documentReader,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher;
        _documentReader = documentReader;
        _clock = clock;
    }

    public Task RefreshMetadataAsync(
        string actorId,
        WorkflowExecutionProjectionContext context,
        CancellationToken ct = default)
    {
        var updatedAt = _clock.UtcNow;
        return RefreshMetadataCoreAsync(actorId, context, updatedAt, ct);
    }

    public Task MarkStoppedAsync(
        string actorId,
        CancellationToken ct = default)
    {
        var updatedAt = _clock.UtcNow;
        return MarkStoppedCoreAsync(actorId, updatedAt, ct);
    }

    private async Task RefreshMetadataCoreAsync(
        string actorId,
        WorkflowExecutionProjectionContext context,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        var report = await _documentReader.GetAsync(actorId, ct) ?? new WorkflowExecutionReport
        {
            Id = actorId,
            RootActorId = actorId,
            Summary = new WorkflowExecutionSummary(),
        };
        report.Id = actorId;
        if (string.IsNullOrWhiteSpace(report.RootActorId))
            report.RootActorId = actorId;
        report.CommandId = context.CommandId;
        report.WorkflowName = context.WorkflowName;
        report.Input = context.Input;
        if (report.CreatedAt == default)
            report.CreatedAt = context.StartedAt;
        report.StartedAt = context.StartedAt;
        if (report.EndedAt < report.StartedAt)
            report.EndedAt = report.StartedAt;

        WorkflowExecutionProjectionMutations.RefreshDerivedFields(report, updatedAt);
        await _writeDispatcher.UpsertAsync(report, ct);
    }

    private async Task MarkStoppedCoreAsync(
        string actorId,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        var report = await _documentReader.GetAsync(actorId, ct) ?? new WorkflowExecutionReport
        {
            Id = actorId,
            RootActorId = actorId,
            Summary = new WorkflowExecutionSummary(),
        };
        report.Id = actorId;
        if (string.IsNullOrWhiteSpace(report.RootActorId))
            report.RootActorId = actorId;
        if (report.CompletionStatus is WorkflowExecutionCompletionStatus.Running or WorkflowExecutionCompletionStatus.Unknown)
            report.CompletionStatus = WorkflowExecutionCompletionStatus.Stopped;

        if (report.EndedAt < report.StartedAt)
            report.EndedAt = updatedAt;

        WorkflowExecutionProjectionMutations.RefreshDerivedFields(report, updatedAt);
        await _writeDispatcher.UpsertAsync(report, ct);
    }
}
