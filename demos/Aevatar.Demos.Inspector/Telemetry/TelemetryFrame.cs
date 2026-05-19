namespace Aevatar.Demos.Inspector.Telemetry;

public sealed record TelemetryFrame(
    string Id,
    string TraceId,
    string SpanId,
    string Name,
    DateTimeOffset Timestamp,
    double DurationMs,
    string Status,
    IReadOnlyDictionary<string, string> Tags);
