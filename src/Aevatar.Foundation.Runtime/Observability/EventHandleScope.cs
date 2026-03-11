using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.Observability;

/// <summary>
/// Unified instrumentation scope for runtime event handling.
/// Composes tracing (Activity), structured logging (log scope), and metrics
/// into a single disposable scope, eliminating duplicate Stopwatch/error-tracking.
/// </summary>
public struct EventHandleScope : IDisposable
{
    private readonly Stopwatch _sw;
    private readonly Activity? _activity;
    private readonly IDisposable? _logScope;
    private readonly string _direction;
    private string _result;
    private bool _disposed;

    public Activity? Activity => _activity;

    private EventHandleScope(
        Stopwatch sw,
        Activity? activity,
        IDisposable? logScope,
        string direction)
    {
        _sw = sw;
        _activity = activity;
        _logScope = logScope;
        _direction = direction;
        _result = AgentMetrics.ResultOk;
        _disposed = false;
    }

    public static EventHandleScope Begin(ILogger logger, string actorId, EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(envelope);

        var activity = AevatarActivitySource.StartHandleEvent(actorId, envelope);
        var logScope = logger.BeginScope(TracingContextHelpers.CreateLogScopeState(envelope));
        return new EventHandleScope(
            Stopwatch.StartNew(),
            activity,
            logScope,
            (envelope.Route?.Direction ?? EventDirection.Unspecified).ToString());
    }

    public void MarkError(Exception ex)
    {
        _result = AgentMetrics.ResultError;
        _activity?.SetTag("aevatar.error", true);
        _activity?.SetTag("aevatar.error.message", ex.Message);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _sw.Stop();
        AgentMetrics.RecordEventHandled(_direction, _result, _sw.Elapsed.TotalMilliseconds);
        _logScope?.Dispose();
        _activity?.Dispose();
    }
}
