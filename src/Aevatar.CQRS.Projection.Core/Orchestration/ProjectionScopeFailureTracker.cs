using Aevatar.CQRS.Projection.Core.Observability;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal sealed class ProjectionScopeFailureTracker
{
    private readonly Func<Google.Protobuf.IMessage, Task> _persistAsync;
    private readonly Func<IProjectionFailureAlertSink?> _alertSinkResolver;
    private readonly Func<ProjectionRuntimeScopeKey> _scopeKeyResolver;
    private readonly Func<int> _failureCountAccessor;
    private readonly Func<int> _diagnosticCountAccessor;
    private readonly Func<ProjectionFailureDiagnostic?> _oldestDiagnosticAccessor;
    private readonly Func<DateTimeOffset?> _oldestFailureAtAccessor;
    private readonly Func<long> _diagnosticDroppedTotalAccessor;

    public ProjectionScopeFailureTracker(
        Func<Google.Protobuf.IMessage, Task> persistAsync,
        Func<IProjectionFailureAlertSink?> alertSinkResolver,
        Func<ProjectionRuntimeScopeKey> scopeKeyResolver,
        Func<int> failureCountAccessor,
        Func<int> diagnosticCountAccessor,
        Func<ProjectionFailureDiagnostic?> oldestDiagnosticAccessor,
        Func<DateTimeOffset?> oldestFailureAtAccessor,
        Func<long> diagnosticDroppedTotalAccessor)
    {
        _persistAsync = persistAsync ?? throw new ArgumentNullException(nameof(persistAsync));
        _alertSinkResolver = alertSinkResolver ?? throw new ArgumentNullException(nameof(alertSinkResolver));
        _scopeKeyResolver = scopeKeyResolver ?? throw new ArgumentNullException(nameof(scopeKeyResolver));
        _failureCountAccessor = failureCountAccessor ?? throw new ArgumentNullException(nameof(failureCountAccessor));
        _diagnosticCountAccessor = diagnosticCountAccessor ?? throw new ArgumentNullException(nameof(diagnosticCountAccessor));
        _oldestDiagnosticAccessor = oldestDiagnosticAccessor ?? throw new ArgumentNullException(nameof(oldestDiagnosticAccessor));
        _oldestFailureAtAccessor = oldestFailureAtAccessor ?? throw new ArgumentNullException(nameof(oldestFailureAtAccessor));
        _diagnosticDroppedTotalAccessor = diagnosticDroppedTotalAccessor ?? throw new ArgumentNullException(nameof(diagnosticDroppedTotalAccessor));
    }

    public async ValueTask RecordAsync(
        string stage,
        string eventId,
        string eventType,
        long sourceVersion,
        string reason,
        EventEnvelope envelope,
        ILogger logger)
    {
        var droppedDiagnostic = _diagnosticCountAccessor() >= ProjectionFailureRetentionPolicy.DefaultMaxRetainedFailures
            ? _oldestDiagnosticAccessor()?.Clone()
            : null;
        var evt = ProjectionScopeFailureLog.BuildFailureEvent(
            stage, eventId, eventType, sourceVersion, reason, envelope);
        await _persistAsync(evt);

        var scopeKey = _scopeKeyResolver();
        ProjectionProcessingMetrics.RecordFailed(
            scopeKey.ProjectionKind,
            eventType,
            _failureCountAccessor(),
            _oldestFailureAtAccessor(),
            addsUnresolvedFailure: true);
        if (droppedDiagnostic != null)
            ProjectionProcessingMetrics.RecordDiagnosticDropped(scopeKey.ProjectionKind, 1);

        var alertSink = _alertSinkResolver();
        if (alertSink == null)
            return;

        await PublishAlertAsync(
            alertSink,
            new ProjectionFailureAlert(
                ProjectionFailureAlertKind.FailureRecorded,
                scopeKey,
                evt.FailureId,
                stage,
                eventId,
                eventType,
                sourceVersion,
                reason,
                _failureCountAccessor(),
                0,
                [],
                _diagnosticDroppedTotalAccessor(),
                DateTimeOffset.UtcNow),
            logger);

        if (droppedDiagnostic != null)
        {
            await PublishAlertAsync(
                alertSink,
                new ProjectionFailureAlert(
                    ProjectionFailureAlertKind.DiagnosticRetentionDropped,
                    scopeKey,
                    evt.FailureId,
                    stage,
                    eventId,
                    eventType,
                    sourceVersion,
                    reason,
                    _failureCountAccessor(),
                    1,
                    [droppedDiagnostic.FailureId],
                    _diagnosticDroppedTotalAccessor(),
                    DateTimeOffset.UtcNow),
                logger);
        }
    }

    public async Task ReplayAsync(
        ProjectionScopeState state,
        int maxItems,
        Func<EventEnvelope, CancellationToken, Task<ProjectionScopeDispatchResult>> dispatchAsync,
        bool includeRetryExhausted = true,
        bool retryExhaustedOnly = false)
    {
        if (state.Failures.Count == 0)
            return;

        var failures = ProjectionScopeFailureLog.GetPendingFailures(
            state,
            maxItems,
            includeRetryExhausted,
            retryExhaustedOnly);
        var resolvedCoordinates = new HashSet<ProjectionFailureSourceCoordinate>();
        foreach (var failure in failures)
        {
            if (failure.Envelope == null)
                continue;

            var sourceCoordinate = TryBuildSourceCoordinate(failure);
            if (sourceCoordinate != null && !resolvedCoordinates.Add(sourceCoordinate))
                continue;

            try
            {
                var result = await dispatchAsync(failure.Envelope, CancellationToken.None);
                if (result.Handled)
                {
                    if (sourceCoordinate != null)
                    {
                        await ResolveMatchingAsync(
                            state,
                            new ProjectionSourceCoordinate
                            {
                                ActorId = sourceCoordinate.SourceActorId,
                                StateVersion = sourceCoordinate.SourceVersion,
                                EventId = sourceCoordinate.EventId,
                            });
                    }
                    else
                    {
                        await _persistAsync(
                            ProjectionScopeFailureLog.BuildReplayResultEvent(failure.FailureId, true));
                        ProjectionProcessingMetrics.RecordResolved(
                            _scopeKeyResolver().ProjectionKind,
                            _failureCountAccessor(),
                            _oldestFailureAtAccessor());
                    }
                }
                else
                {
                    await RecordReplayFailureAsync(
                        failure,
                        "Replay did not produce a materialization result.");
                    break;
                }
            }
            catch (ProjectionScopeStatusRouteBlockedException)
            {
                // The status route is blocked for a cutover: nothing was re-attempted, so the
                // refusal must not consume the failure's replay budget. The replay is simply
                // retried after the flip (activation resumes the cutover first).
                break;
            }
            catch (Exception ex)
            {
                await RecordReplayFailureAsync(failure, ex.Message);
                break;
            }
        }
    }

    private static ProjectionFailureSourceCoordinate? TryBuildSourceCoordinate(
        ProjectionScopeFailure failure)
    {
        if (string.IsNullOrWhiteSpace(failure.SourceActorId) ||
            failure.SourceVersion <= 0 ||
            string.IsNullOrWhiteSpace(failure.EventId))
        {
            return null;
        }

        return new ProjectionFailureSourceCoordinate(
            failure.SourceActorId,
            failure.SourceVersion,
            failure.EventId);
    }

    public async Task ResolveMatchingAsync(
        ProjectionScopeState state,
        ProjectionSourceCoordinate source)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(source);

        var matchingFailureIds = state.Failures
            .Where(failure =>
                failure.SourceVersion == source.StateVersion &&
                string.Equals(failure.SourceActorId, source.ActorId, StringComparison.Ordinal) &&
                string.Equals(failure.EventId, source.EventId, StringComparison.Ordinal))
            .Select(static failure => failure.FailureId)
            .Where(static failureId => !string.IsNullOrWhiteSpace(failureId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var failureId in matchingFailureIds)
        {
            await _persistAsync(
                ProjectionScopeFailureLog.BuildReplayResultEvent(failureId, true));
            ProjectionProcessingMetrics.RecordResolved(
                _scopeKeyResolver().ProjectionKind,
                _failureCountAccessor(),
                _oldestFailureAtAccessor());
        }
    }

    private async Task RecordReplayFailureAsync(ProjectionScopeFailure failure, string reason)
    {
        var becomesExhausted = !failure.RetryExhausted &&
                               failure.Attempts + 1 >= ProjectionFailureRetentionPolicy.DefaultMaxReplayAttempts;
        await _persistAsync(
            ProjectionScopeFailureLog.BuildReplayResultEvent(failure.FailureId, false, reason));
        ProjectionProcessingMetrics.RecordFailed(
            _scopeKeyResolver().ProjectionKind,
            failure.EventType,
            _failureCountAccessor(),
            _oldestFailureAtAccessor(),
            addsUnresolvedFailure: false,
            retryExhausted: becomesExhausted);
    }

    private static async Task PublishAlertAsync(
        IProjectionFailureAlertSink alertSink,
        ProjectionFailureAlert alert,
        ILogger logger)
    {
        try
        {
            await alertSink.PublishAsync(alert, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Projection failure alert publishing failed. alertKind={AlertKind}",
                alert.Kind);
        }
    }

    private sealed record ProjectionFailureSourceCoordinate(
        string SourceActorId,
        long SourceVersion,
        string EventId);
}
