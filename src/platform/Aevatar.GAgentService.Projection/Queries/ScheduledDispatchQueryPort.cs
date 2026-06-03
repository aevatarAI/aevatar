using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ScheduledDispatchQueryPort : IScheduledDispatchQueryPort
{
    private readonly IProjectionDocumentReader<ScheduledDispatchDocument, string> _documentReader;

    public ScheduledDispatchQueryPort(IProjectionDocumentReader<ScheduledDispatchDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<ScheduledDispatchDetail?> GetAsync(string scheduleId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scheduleId))
            return null;

        var document = await _documentReader.GetAsync(scheduleId.Trim(), ct);
        return document == null ? null : MapDetail(document);
    }

    public async Task<ScheduledDispatchListResult> ListAsync(
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

        return new ScheduledDispatchListResult(
            result.Items.Select(MapSummary).ToArray(),
            result.NextCursor,
            result.TotalCount);
    }

    private static ScheduledDispatchDetail MapDetail(ScheduledDispatchDocument document) =>
        new(
            MapSummary(document),
            document.FireRecords
                .Select(MapFireRecord)
                .OrderByDescending(static x => x.CompletedAt)
                .ToArray());

    private static ScheduledDispatchSummary MapSummary(ScheduledDispatchDocument document) =>
        new(
            document.ScheduleId,
            document.DisplayName ?? string.Empty,
            ParseTargetKind(document.TargetKind),
            document.TargetActorId ?? string.Empty,
            document.PayloadTypeUrl ?? string.Empty,
            document.ServiceKey ?? string.Empty,
            document.ServiceId ?? string.Empty,
            document.ServiceEndpointId ?? string.Empty,
            document.CronExpression ?? string.Empty,
            document.Timezone ?? string.Empty,
            document.Enabled,
            document.CreatedAt,
            document.UpdatedAt,
            document.NextFireAt,
            document.LastFireAt,
            document.LastTargetActorId ?? string.Empty,
            document.LastCommandId ?? string.Empty,
            document.LastCorrelationId ?? string.Empty,
            document.LastError ?? string.Empty,
            document.FireCount,
            document.FailureCount,
            document.Headers.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            document.ScheduleActorId ?? string.Empty);

    private static ScheduledDispatchFireRecord MapFireRecord(ScheduledDispatchFireRecordDocument document) =>
        new(
            document.ScheduledFireAt,
            document.CompletedAt,
            document.IdempotencyKey ?? string.Empty,
            document.TargetActorId ?? string.Empty,
            document.CommandId ?? string.Empty,
            document.CorrelationId ?? string.Empty,
            document.Error ?? string.Empty,
            document.Manual);

    private static ScheduledDispatchTargetKind ParseTargetKind(string? value) =>
        Enum.TryParse<ScheduledDispatchTargetKind>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ScheduledDispatchTargetKind.Envelope;
}
