using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.BackendConsole.Hosting;
using Aevatar.GAgents.StatusDashboard;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Mainnet.Host.Api.Status;

public static class StatusEndpoints
{
    private const int HistorySampleCount = 120;

    private static readonly BackendConsoleAsset PageAsset = new(
        LogicalName: "status",
        Assembly: typeof(StatusEndpoints).Assembly,
        ResourceSuffix: "Status.status.html",
        ContentType: "text/html",
        InjectHostConfiguration: false);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/status", async (HttpContext ctx, IHealthStatusQueryPort port, CancellationToken ct) =>
        {
            var docs = await port.ListAllAsync(ct);
            var response = StatusResponse.Build(docs);
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(ctx.Response.Body, response, JsonOptions, ct);
        })
        .WithTags("Status")
        .WithName("GetStatusJson")
        .WithSummary("Aggregated health snapshot for all configured probe targets.")
        .AllowAnonymous();

        app.MapGet("/status", GetStatusHtml)
        .WithTags("Status")
        .WithName("GetStatusHtml")
        .WithSummary("Sub2api-style HTML dashboard for service health.")
        .AllowAnonymous();

        return app;
    }

    internal static IResult GetStatusHtml(
        HttpContext http,
        [FromServices] IBackendConsoleAssetService assets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(assets);
        return assets.Serve(PageAsset);
    }

    private sealed record StatusResponse(
        [property: JsonPropertyName("generated_at")] DateTimeOffset GeneratedAt,
        [property: JsonPropertyName("overall")] string Overall,
        [property: JsonPropertyName("counts")] StatusCounts Counts,
        [property: JsonPropertyName("targets")] IReadOnlyList<StatusTarget> Targets)
    {
        public static StatusResponse Build(IReadOnlyList<HealthProbeOperationalSnapshot> snapshots)
        {
            var targets = snapshots
                .OrderBy(d => d.Target?.Category, StringComparer.Ordinal)
                .ThenBy(d => d.Target?.Slug, StringComparer.Ordinal)
                .Select(StatusTarget.From)
                .ToArray();

            var counts = StatusCounts.Tally(targets);
            var overall = ComputeOverall(targets);

            return new StatusResponse(DateTimeOffset.UtcNow, overall, counts, targets);
        }

        // Honest, severity-weighted roll-up. Only targets with a *known* status count, so an
        // unconfigured canary (status "unknown") can never force the board red. A "critical"
        // target being down is the only thing that blacks out the whole board; lesser-severity
        // failures and critical degradations surface as "degraded" without masking a real outage.
        private static string ComputeOverall(IReadOnlyList<StatusTarget> targets)
        {
            var anyKnown = false;
            var anyOk = false;
            var criticalDown = false;
            var degraded = false;

            foreach (var t in targets)
            {
                if (!t.Enabled) continue;
                var isCritical = string.Equals(t.Severity, "critical", StringComparison.Ordinal);
                switch (t.Status)
                {
                    case "ok":
                        anyKnown = true;
                        anyOk = true;
                        break;
                    case "degraded":
                        anyKnown = true;
                        degraded = true;
                        break;
                    case "down":
                        anyKnown = true;
                        if (isCritical) criticalDown = true;
                        else degraded = true;
                        break;
                    default:
                        // "unknown" — excluded from the verdict.
                        break;
                }
            }

            if (!anyKnown) return "unknown";
            if (criticalDown) return "down";
            if (degraded) return "degraded";
            return anyOk ? "ok" : "unknown";
        }
    }

    private sealed record StatusCounts(
        [property: JsonPropertyName("ok")] int Ok,
        [property: JsonPropertyName("degraded")] int Degraded,
        [property: JsonPropertyName("down")] int Down,
        [property: JsonPropertyName("unknown")] int Unknown,
        [property: JsonPropertyName("total")] int Total)
    {
        public static StatusCounts Tally(IReadOnlyList<StatusTarget> targets)
        {
            int ok = 0, degraded = 0, down = 0, unknown = 0;
            foreach (var t in targets)
            {
                switch (t.Status)
                {
                    case "ok": ok++; break;
                    case "degraded": degraded++; break;
                    case "down": down++; break;
                    default: unknown++; break;
                }
            }
            return new StatusCounts(ok, degraded, down, unknown, targets.Count);
        }
    }

    private sealed record StatusTarget(
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("severity")] string Severity,
        [property: JsonPropertyName("probe")] string Probe,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("interval_seconds")] int IntervalSeconds,
        [property: JsonPropertyName("latency_ms")] int LatencyMs,
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("error_message")] string? ErrorMessage,
        [property: JsonPropertyName("consecutive_failures")] int ConsecutiveFailures,
        [property: JsonPropertyName("last_check_at")] DateTimeOffset? LastCheckAt,
        [property: JsonPropertyName("last_success_at")] DateTimeOffset? LastSuccessAt,
        [property: JsonPropertyName("availability_percent")] double? AvailabilityPercent,
        [property: JsonPropertyName("history")] IReadOnlyList<StatusSample> History)
    {
        public static StatusTarget From(HealthProbeOperationalSnapshot snapshot)
        {
            var target = snapshot.Target ?? new HealthProbeTargetDescriptor();
            var outcome = snapshot.LastOutcome;
            var history = BuildHistory(snapshot);
            return new StatusTarget(
                target.Slug,
                string.IsNullOrWhiteSpace(target.DisplayName) ? target.Slug : target.DisplayName,
                string.IsNullOrWhiteSpace(target.Category) ? "upstream" : target.Category,
                string.IsNullOrWhiteSpace(target.Severity) ? "standard" : target.Severity,
                target.ProbeKind,
                MapStatus(outcome?.Status ?? HealthOutcomeStatus.Unknown),
                target.Enabled,
                target.IntervalSeconds,
                outcome?.LatencyMs ?? 0,
                NullIfBlank(outcome?.Detail),
                NullIfBlank(outcome?.ErrorMessage),
                snapshot.ConsecutiveFailures,
                ToDateTime(snapshot.LastCheckAt),
                ToDateTime(snapshot.LastSuccessAt),
                CalculateAvailability(history),
                history);
        }

        private static IReadOnlyList<StatusSample> BuildHistory(HealthProbeOperationalSnapshot snapshot)
        {
            var history = snapshot.RecentOutcomes
                .Select(StatusSample.From)
                .TakeLast(HistorySampleCount)
                .ToArray();

            if (history.Length > 0 || ToDateTime(snapshot.LastCheckAt) is not { } lastCheckAt)
            {
                return history;
            }

            var outcome = snapshot.LastOutcome;

            return
            [
                new StatusSample(
                    MapStatus(outcome?.Status ?? HealthOutcomeStatus.Unknown),
                    outcome?.LatencyMs ?? 0,
                    NullIfBlank(outcome?.Detail),
                    NullIfBlank(outcome?.ErrorMessage),
                    lastCheckAt),
            ];
        }

        private static double? CalculateAvailability(IReadOnlyList<StatusSample> history)
        {
            var known = history.Count(static sample => sample.Status != "unknown");
            if (known == 0) return null;

            var ok = history.Count(static sample => sample.Status == "ok");
            return Math.Round(ok * 100d / known, 1, MidpointRounding.AwayFromZero);
        }
    }

    private sealed record StatusSample(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("latency_ms")] int LatencyMs,
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("error_message")] string? ErrorMessage,
        [property: JsonPropertyName("observed_at")] DateTimeOffset? ObservedAt)
    {
        public static StatusSample From(HealthProbeOutcome outcome) =>
            new(
                MapStatus(outcome.Status),
                outcome.LatencyMs,
                NullIfBlank(outcome.Detail),
                NullIfBlank(outcome.ErrorMessage),
                ToDateTime(outcome.ObservedAt));
    }

    private static string MapStatus(HealthOutcomeStatus status) => status switch
    {
        HealthOutcomeStatus.Ok => "ok",
        HealthOutcomeStatus.Degraded => "degraded",
        HealthOutcomeStatus.Down => "down",
        _ => "unknown",
    };

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static DateTimeOffset? ToDateTime(Timestamp? t) =>
        t == null ? null : t.ToDateTimeOffset();
}
