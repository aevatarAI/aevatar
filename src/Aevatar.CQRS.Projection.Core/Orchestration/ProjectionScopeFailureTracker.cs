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
        Func<EventEnvelope, CancellationToken, Task<ProjectionScopeDispatchResult>> dispatchAsync)
    {
        if (state.Failures.Count == 0)
            return;

        var failures = ProjectionScopeFailureLog.GetPendingFailures(state, maxItems);
        foreach (var failure in failures)
        {
            if (failure.Envelope == null)
                continue;

            try
            {
                var result = await dispatchAsync(failure.Envelope, CancellationToken.None);
                if (result.Handled)
                {
                    await _persistAsync(
                        ProjectionScopeFailureLog.BuildReplayResultEvent(failure.FailureId, true));
                    ProjectionProcessingMetrics.RecordResolved(
                        _scopeKeyResolver().ProjectionKind,
                        _failureCountAccessor(),
                        _oldestFailureAtAccessor());
                }
                else
                {
                    await RecordReplayFailureAsync(
                        failure,
                        "Replay did not produce a materialization result.");
                }
            }
            catch (Exception ex)
            {
                await RecordReplayFailureAsync(failure, ex.Message);
            }
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
}
