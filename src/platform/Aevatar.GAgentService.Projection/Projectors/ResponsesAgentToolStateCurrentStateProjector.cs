using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class ResponsesAgentToolStateCurrentStateProjector
    : ICurrentStateProjectionMaterializer<ResponsesAgentToolStateCurrentStateProjectionContext>
{
    private readonly IProjectionWriteDispatcher<ResponsesAgentToolStateCurrentStateReadModel> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ResponsesAgentToolStateCurrentStateProjector(
        IProjectionWriteDispatcher<ResponsesAgentToolStateCurrentStateReadModel> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        ResponsesAgentToolStateCurrentStateProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<ResponsesAgentToolState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state?.Record == null)
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var document = new ResponsesAgentToolStateCurrentStateReadModel
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            ScopeId = state.Record.ScopeId,
            OwnerSubject = state.Record.OwnerSubject,
            CreatedAt = state.Record.CreatedAt?.ToDateTimeOffset() ?? updatedAt,
            UpdatedAt = state.Record.UpdatedAt?.ToDateTimeOffset() ?? updatedAt,
            Todos = state.TodoItems.Select(static todo => new ResponsesTodoItemReadModel
            {
                Id = todo.Id,
                Content = todo.Content,
                Status = todo.Status,
                SourceResponseId = todo.SourceResponseId,
                CreatedAt = todo.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                UpdatedAt = todo.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            }).ToList(),
            WebTraces = state.WebTraces.Select(static trace => new ResponsesWebTraceReadModel
            {
                TraceId = trace.TraceId,
                SourceResponseId = trace.SourceResponseId,
                ToolName = trace.ToolName,
                CacheKey = trace.CacheKey,
                Url = trace.Url,
                Query = trace.Query,
                CacheHit = trace.CacheHit,
                Result = trace.Result?.Clone(),
                TypedResult = trace.TypedResult?.Clone(),
                ObservedAt = trace.ObservedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            }).ToList(),
            WebCacheEntries = state.WebCacheEntries.Select(static entry => new ResponsesWebCacheEntryReadModel
            {
                CacheKey = entry.CacheKey,
                ToolName = entry.ToolName,
                Url = entry.Url,
                Query = entry.Query,
                Result = entry.Result?.Clone(),
                TypedResult = entry.TypedResult?.Clone(),
                CachedAt = entry.CachedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                LastHitAt = entry.LastHitAt?.ToDateTimeOffset(),
                HitCount = entry.HitCount,
            }).ToList(),
        };

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
