using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.GAgents.StatusDashboard;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Mainnet.Host.Api.Status;

public static class StatusEndpoints
{
    private const int HistorySampleCount = 120;

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

        app.MapGet("/status", (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            return ctx.Response.WriteAsync(StatusHtml.Page, Encoding.UTF8, ctx.RequestAborted);
        })
        .WithTags("Status")
        .WithName("GetStatusHtml")
        .WithSummary("Sub2api-style HTML dashboard for service health.")
        .AllowAnonymous();

        return app;
    }

    private sealed record StatusResponse(
        [property: JsonPropertyName("generated_at")] DateTimeOffset GeneratedAt,
        [property: JsonPropertyName("overall")] string Overall,
        [property: JsonPropertyName("counts")] StatusCounts Counts,
        [property: JsonPropertyName("targets")] IReadOnlyList<StatusTarget> Targets)
    {
        public static StatusResponse Build(IReadOnlyList<HealthProbeTargetDocument> documents)
        {
            var targets = documents
                .OrderBy(d => d.Category, StringComparer.Ordinal)
                .ThenBy(d => d.Slug, StringComparer.Ordinal)
                .Select(StatusTarget.From)
                .ToArray();

            var counts = StatusCounts.Tally(targets);
            var overall = counts switch
            {
                { Total: 0 } => "unknown",
                { Down: > 0 } => "down",
                { Degraded: > 0 } => "degraded",
                { Unknown: > 0, Ok: 0 } => "unknown",
                _ => "ok",
            };

            return new StatusResponse(DateTimeOffset.UtcNow, overall, counts, targets);
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
        public static StatusTarget From(HealthProbeTargetDocument d)
        {
            var history = BuildHistory(d);
            return new StatusTarget(
                d.Slug,
                string.IsNullOrWhiteSpace(d.DisplayName) ? d.Slug : d.DisplayName,
                string.IsNullOrWhiteSpace(d.Category) ? "upstream" : d.Category,
                d.ProbeKind,
                MapStatus(d.Status),
                d.Enabled,
                d.IntervalSeconds,
                d.LatencyMs,
                NullIfBlank(d.Detail),
                NullIfBlank(d.ErrorMessage),
                d.ConsecutiveFailures,
                ToDateTime(d.LastCheckAt),
                ToDateTime(d.LastSuccessAt),
                CalculateAvailability(history),
                history);
        }

        private static IReadOnlyList<StatusSample> BuildHistory(HealthProbeTargetDocument d)
        {
            var history = d.RecentOutcomes
                .Select(StatusSample.From)
                .TakeLast(HistorySampleCount)
                .ToArray();

            if (history.Length > 0 || ToDateTime(d.LastCheckAt) is not { } lastCheckAt)
            {
                return history;
            }

            return
            [
                new StatusSample(
                    MapStatus(d.Status),
                    d.LatencyMs,
                    NullIfBlank(d.Detail),
                    NullIfBlank(d.ErrorMessage),
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
