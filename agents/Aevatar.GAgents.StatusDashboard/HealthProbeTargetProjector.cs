using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.StatusDashboard;

/// <summary>
/// Materializes <see cref="HealthProbeTargetState"/> committed events into
/// per-actor <see cref="HealthProbeTargetDocument"/> entries so the
/// <c>/api/status</c> endpoint can read the latest snapshot without priming
/// the projection at query time.
/// </summary>
public sealed class HealthProbeTargetProjector
    : ICurrentStateProjectionMaterializer<HealthProbeMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<HealthProbeTargetDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public HealthProbeTargetProjector(
        IProjectionWriteDispatcher<HealthProbeTargetDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        HealthProbeMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<HealthProbeTargetState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null ||
            state.Spec == null ||
            string.IsNullOrWhiteSpace(state.Spec.Slug))
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var outcome = state.LastOutcome;

        var document = new HealthProbeTargetDocument
        {
            Id = state.Spec.Slug,
            Slug = state.Spec.Slug,
            DisplayName = state.Spec.DisplayName ?? string.Empty,
            Category = state.Spec.Category ?? string.Empty,
            ProbeKind = state.Spec.ProbeKind ?? string.Empty,
            IntervalSeconds = state.Spec.IntervalSeconds,
            Enabled = state.Spec.Enabled,
            Status = outcome?.Status ?? HealthOutcomeStatus.Unknown,
            LatencyMs = outcome?.LatencyMs ?? 0,
            Detail = outcome?.Detail ?? string.Empty,
            ErrorMessage = outcome?.ErrorMessage ?? string.Empty,
            ConsecutiveFailures = state.ConsecutiveFailures,
            LastCheckAt = state.LastCheckAt,
            LastSuccessAt = state.LastSuccessAt,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            ActorId = context.RootActorId,
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(updatedAt),
        };
        document.RecentOutcomes.AddRange(state.RecentOutcomes.Select(static outcome => outcome.Clone()));

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
