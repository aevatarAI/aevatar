using System.Diagnostics.Metrics;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public static class NyxIdRelayMetrics
{
    public const string MeterName = "Aevatar.Channel.NyxIdRelay";
    public const string CallbackJwtValidationFailuresTotal = "callback_jwt_validation_failures_total";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> CallbackJwtValidationFailures = Meter.CreateCounter<long>(
        CallbackJwtValidationFailuresTotal,
        description: "NyxID relay callback JWT validation failures.");

    public static void RecordCallbackJwtValidationFailure(string reason)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
        CallbackJwtValidationFailures.Add(1, new KeyValuePair<string, object?>("reason", normalizedReason));
    }
}
