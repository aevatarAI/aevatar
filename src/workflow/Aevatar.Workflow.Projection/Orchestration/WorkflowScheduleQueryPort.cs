using Aevatar.Workflow.Application.Abstractions.Schedules;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowScheduleQueryPort : IWorkflowScheduleQueryPort
{
    private readonly IProjectionDocumentReader<WorkflowScheduleDocument, string> _documentReader;

    public WorkflowScheduleQueryPort(IProjectionDocumentReader<WorkflowScheduleDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<WorkflowScheduleDetail?> GetAsync(string scheduleId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scheduleId))
            return null;

        var document = await _documentReader.GetAsync(scheduleId.Trim(), ct);
        return document == null ? null : MapDetail(document);
    }

    public async Task<WorkflowScheduleListResult> ListAsync(
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default)
    {
        var result = await _documentReader.QueryAsync(new ProjectionDocumentQuery
        {
            Take = Math.Clamp(take, 1, 200),
            Cursor = cursor,
            IncludeTotalCount = includeTotalCount,
        }, ct);

        return new WorkflowScheduleListResult(
            result.Items.Select(MapSummary).ToArray(),
            result.NextCursor,
            result.TotalCount);
    }

    private static WorkflowScheduleDetail MapDetail(WorkflowScheduleDocument document) =>
        new(
            MapSummary(document),
            document.FireRecords
                .Select(MapFireRecord)
                .OrderByDescending(static x => x.CompletedAt)
                .ToArray());

    private static WorkflowScheduleSummary MapSummary(WorkflowScheduleDocument document) =>
        new(
            document.ScheduleId,
            document.DisplayName ?? string.Empty,
            document.WorkflowName ?? string.Empty,
            document.CronExpression ?? string.Empty,
            document.Timezone ?? string.Empty,
            document.Enabled,
            document.CreatedAt,
            document.UpdatedAt,
            document.NextFireAt,
            document.LastFireAt,
            document.LastRunActorId ?? string.Empty,
            document.LastCommandId ?? string.Empty,
            document.LastCorrelationId ?? string.Empty,
            document.LastError ?? string.Empty,
            document.FireCount,
            document.FailureCount,
            document.Headers.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            document.ScopeId ?? string.Empty,
            document.SourceActorId ?? string.Empty,
            document.ScheduleActorId ?? string.Empty,
            document.TargetActorId ?? string.Empty);

    private static WorkflowScheduleFireRecord MapFireRecord(WorkflowScheduleFireRecordDocument document) =>
        new(
            document.ScheduledFireAt,
            document.CompletedAt,
            document.IdempotencyKey ?? string.Empty,
            document.RunActorId ?? string.Empty,
            document.CommandId ?? string.Empty,
            document.CorrelationId ?? string.Empty,
            document.Error ?? string.Empty,
            document.Manual);
}
