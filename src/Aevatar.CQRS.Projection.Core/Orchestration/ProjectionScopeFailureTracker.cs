using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal sealed class ProjectionScopeFailureTracker
{
    private readonly Func<Google.Protobuf.IMessage, Task> _persistAsync;
    private readonly Func<IProjectionFailureAlertSink?> _alertSinkResolver;
    private readonly Func<ProjectionRuntimeScopeKey> _scopeKeyResolver;
    private readonly Func<int> _failureCountAccessor;

    public ProjectionScopeFailureTracker(
        Func<Google.Protobuf.IMessage, Task> persistAsync,
        Func<IProjectionFailureAlertSink?> alertSinkResolver,
        Func<ProjectionRuntimeScopeKey> scopeKeyResolver,
        Func<int> failureCountAccessor)
    {
        _persistAsync = persistAsync ?? throw new ArgumentNullException(nameof(persistAsync));
        _alertSinkResolver = alertSinkResolver ?? throw new ArgumentNullException(nameof(alertSinkResolver));
        _scopeKeyResolver = scopeKeyResolver ?? throw new ArgumentNullException(nameof(scopeKeyResolver));
        _failureCountAccessor = failureCountAccessor ?? throw new ArgumentNullException(nameof(failureCountAccessor));
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
        var evt = ProjectionScopeFailureLog.BuildFailureEvent(
            stage, eventId, eventType, sourceVersion, reason, envelope);
        await _persistAsync(evt);

        var alertSink = _alertSinkResolver();
        if (alertSink == null)
            return;

        try
        {
            await alertSink.PublishAsync(
                new ProjectionFailureAlert(
                    _scopeKeyResolver(),
                    evt.FailureId,
                    stage,
                    eventId,
                    eventType,
                    sourceVersion,
                    reason,
                    Math.Min(ProjectionFailureRetentionPolicy.DefaultMaxRetainedFailures, _failureCountAccessor() + 1),
                    DateTimeOffset.UtcNow),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Projection failure alert publishing failed.");
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
                }
            }
            catch (Exception ex)
            {
                await _persistAsync(
                    ProjectionScopeFailureLog.BuildReplayResultEvent(failure.FailureId, false, ex.Message));
            }
        }
    }
}
