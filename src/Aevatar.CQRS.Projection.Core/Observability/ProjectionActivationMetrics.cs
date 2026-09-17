using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aevatar.CQRS.Projection.Core.Observability;

internal static class ProjectionActivationMetrics
{
    public const string AuthorityLookupStage = "authority_lookup";
    public const string ExistenceLookupStage = "existence_lookup";
    public const string KindVerificationStage = "kind_verification";
    public const string DispatchAdmissionStage = "dispatch_admission";
    public const string RelayReadinessStage = "relay_readiness";
    public const string ReleaseReadinessStage = "release_readiness";

    private static readonly Meter Meter = new(ProjectionProcessingMetrics.MeterName, "1.0.0");
    private static readonly Histogram<double> StageDuration = Meter.CreateHistogram<double>(
        "aevatar.projection.activation.stage.duration",
        unit: "ms",
        description: "Projection activation stage duration.");
    private static readonly Counter<long> Results = Meter.CreateCounter<long>(
        "aevatar.projection.activation.result.total",
        description: "Projection activation warm/cold results.");

    public static long StartTimestamp() => Stopwatch.GetTimestamp();

    public static void RecordStage(
        string stage,
        long startedAt,
        ProjectionRuntimeMode mode,
        string outcome)
    {
        try
        {
            StageDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("stage", stage),
                new KeyValuePair<string, object?>("outcome", outcome),
                new KeyValuePair<string, object?>("mode", Mode(mode)));
        }
        catch (Exception ex)
        {
            TryTraceWarning("Projection activation stage metric failed: {0}", ex.Message);
        }
    }

    public static void RecordResult(string path, ProjectionRuntimeMode mode, string outcome)
    {
        try
        {
            Results.Add(
                1,
                new KeyValuePair<string, object?>("path", path),
                new KeyValuePair<string, object?>("outcome", outcome),
                new KeyValuePair<string, object?>("mode", Mode(mode)));
        }
        catch (Exception ex)
        {
            TryTraceWarning("Projection activation result metric failed: {0}", ex.Message);
        }
    }

    private static string Mode(ProjectionRuntimeMode mode) => mode switch
    {
        ProjectionRuntimeMode.DurableMaterialization => "durable",
        ProjectionRuntimeMode.SessionObservation => "session",
        _ => "unknown",
    };

    private static void TryTraceWarning(string format, string message)
    {
        try
        {
            Trace.TraceWarning(format, message);
        }
        catch (Exception)
        {
            return;
        }
    }
}
