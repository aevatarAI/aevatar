using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class ResponseSessionCurrentStateProjector
    : ICurrentStateProjectionMaterializer<ResponseSessionCurrentStateProjectionContext>
{
    private readonly IProjectionWriteDispatcher<ResponseSessionCurrentStateReadModel> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ResponseSessionCurrentStateProjector(
        IProjectionWriteDispatcher<ResponseSessionCurrentStateReadModel> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        ResponseSessionCurrentStateProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<ResponseSessionState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state?.Record == null)
        {
            return;
        }

        var record = state.Record;
        if (string.IsNullOrWhiteSpace(record.ResponseId) ||
            string.IsNullOrWhiteSpace(record.ScopeId) ||
            string.IsNullOrWhiteSpace(record.OwnerSubject))
        {
            return;
        }

        var observedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var updatedAt = record.UpdatedAt?.ToDateTimeOffset()
                        ?? record.CreatedAt?.ToDateTimeOffset()
                        ?? observedAt;
        var document = new ResponseSessionCurrentStateReadModel
        {
            Id = ResponseSessionIds.BuildKey(record.ResponseId),
            ActorId = context.RootActorId,
            ResponseId = record.ResponseId,
            ScopeId = record.ScopeId ?? string.Empty,
            OwnerSubject = record.OwnerSubject ?? string.Empty,
            OriginKind = (int)record.OriginKind,
            PreviousResponseId = record.PreviousResponseId ?? string.Empty,
            Status = (int)record.Status,
            CreatedAt = record.CreatedAt?.ToDateTimeOffset() ?? observedAt,
            UpdatedAt = updatedAt,
            CancelledAt = record.CancelledAt?.ToDateTimeOffset(),
            TtlSeconds = (long)(record.Ttl?.ToTimeSpan().TotalSeconds ?? 0),
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
        };
        document.ForwardedToolCalls = state.ForwardedToolCalls
            .Select(static call => new ResponseSessionForwardedToolCallReadModel
            {
                CallId = call.CallId ?? string.Empty,
                ToolName = call.ToolName ?? string.Empty,
                SchemaHash = call.SchemaHash ?? string.Empty,
                ArgumentsJson = call.ArgumentsJson ?? string.Empty,
                Status = (int)call.Status,
                ResultJson = call.ResultJson ?? string.Empty,
                Expiry = call.Expiry?.ToDateTimeOffset(),
                EmittedAt = call.EmittedAt?.ToDateTimeOffset(),
                ReceivedAt = call.ReceivedAt?.ToDateTimeOffset(),
                ResolvedAt = call.ResolvedAt?.ToDateTimeOffset(),
            })
            .ToArray();

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
