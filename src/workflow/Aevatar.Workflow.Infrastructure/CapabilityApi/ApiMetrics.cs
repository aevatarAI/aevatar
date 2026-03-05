using System.Diagnostics.Metrics;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class ApiMetrics
{
    private static readonly Meter Meter = new("Aevatar.Api", "1.0.0");
    internal const string TransportTag = "transport";
    internal const string ResultTag = "result";
    internal const string TransportHttp = "http";
    internal const string TransportWebSocket = "ws";
    internal const string ResultOk = "ok";
    internal const string ResultError = "error";

    public static readonly Counter<long> RequestsTotal = Meter.CreateCounter<long>(
        "aevatar.api.requests_total",
        description: "Total API requests by transport and result.");

    public static readonly Histogram<double> RequestDurationMs = Meter.CreateHistogram<double>(
        "aevatar.api.request_duration_ms",
        description: "API request duration in milliseconds.");

    public static readonly Histogram<double> FirstResponseDurationMs = Meter.CreateHistogram<double>(
        "aevatar.api.first_response_duration_ms",
        description: "API first-response duration in milliseconds.");

    public static void RecordRequest(string transport, string result, double durationMs)
    {
        RequestsTotal.Add(1,
        [
            new(TransportTag, transport),
            new(ResultTag, result),
        ]);
        RequestDurationMs.Record(durationMs,
        [
            new(TransportTag, transport),
        ]);
    }

    public static void RecordFirstResponse(string transport, string result, double durationMs)
    {
        FirstResponseDurationMs.Record(durationMs,
        [
            new(TransportTag, transport),
            new(ResultTag, result),
        ]);
    }

    public static string ResolveResult(int statusCode)
    {
        return statusCode >= 500 ? ResultError : ResultOk;
    }
}
