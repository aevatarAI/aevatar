using System.Diagnostics;
using System.Threading;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

/// <summary>
/// Unified instrumentation scope for API request handling.
/// Encapsulates stopwatch, result tracking, first-response recording,
/// and auto-records the request metric on dispose.
/// Reference type so mutations propagate through callee boundaries.
/// </summary>
internal sealed class ApiRequestScope : IDisposable
{
    private readonly Stopwatch _sw;
    private readonly string _transport;
    private string _result;
    private int _firstResponseRecorded;
    private bool _disposed;

    private ApiRequestScope(string transport)
    {
        _sw = Stopwatch.StartNew();
        _transport = transport;
        _result = ApiMetrics.ResultOk;
    }

    public static ApiRequestScope BeginHttp() => new(ApiMetrics.TransportHttp);

    public static ApiRequestScope BeginWebSocket() => new(ApiMetrics.TransportWebSocket);

    public double ElapsedMs => _sw.Elapsed.TotalMilliseconds;

    public void MarkResult(int statusCode)
    {
        _result = ApiMetrics.ResolveResult(statusCode);
    }

    public void MarkError()
    {
        _result = ApiMetrics.ResultError;
    }

    /// <summary>
    /// Records the first-response metric exactly once (thread-safe).
    /// Uses the current result classification. Subsequent calls are no-ops.
    /// </summary>
    public void RecordFirstResponse()
    {
        if (Interlocked.CompareExchange(ref _firstResponseRecorded, 1, 0) == 0)
            ApiMetrics.RecordFirstResponse(_transport, _result, _sw.Elapsed.TotalMilliseconds);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _sw.Stop();
        ApiMetrics.RecordRequest(_transport, _result, _sw.Elapsed.TotalMilliseconds);
    }
}
