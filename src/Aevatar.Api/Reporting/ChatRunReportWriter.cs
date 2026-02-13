// ─────────────────────────────────────────────────────────────
// ChatRunReportWriter — 将 ChatRunReport 写入 JSON 与 HTML
// ─────────────────────────────────────────────────────────────

using System.Net;
using System.Text;
using System.Text.Json;

namespace Aevatar.Api.Reporting;

/// <summary>Writes ChatRunReport to JSON and HTML files.</summary>
public static class ChatRunReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
    };

    public static (string JsonPath, string HtmlPath) BuildDefaultPaths(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        return (
            Path.Combine(outputDirectory, $"chat-run-{stamp}.json"),
            Path.Combine(outputDirectory, $"chat-run-{stamp}.html"));
    }

    public static async Task WriteAsync(ChatRunReport report, string jsonPath, string htmlPath)
    {
        var dir = Path.GetDirectoryName(jsonPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        dir = Path.GetDirectoryName(htmlPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(jsonPath, json);
        await File.WriteAllTextAsync(htmlPath, BuildHtml(report));
    }

    private static string BuildHtml(ChatRunReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html><head><meta charset=\"utf-8\"/>");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>");
        sb.AppendLine("<title>Chat Run Report</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:ui-sans-serif,system-ui;margin:24px;background:#0b1020;color:#dbeafe}");
        sb.AppendLine("h1,h2{margin:0 0 12px}");
        sb.AppendLine(".card{background:#111827;border:1px solid #334155;border-radius:10px;padding:14px;margin:14px 0}");
        sb.AppendLine("table{width:100%;border-collapse:collapse}");
        sb.AppendLine("th,td{border-bottom:1px solid #334155;padding:6px 8px;text-align:left;font-size:13px;vertical-align:top}");
        sb.AppendLine(".muted{color:#93a4bf}");
        sb.AppendLine("code{color:#93c5fd}");
        sb.AppendLine("pre{white-space:pre-wrap;word-break:break-word;background:#0f172a;border:1px solid #334155;border-radius:8px;padding:10px}");
        sb.AppendLine("details{margin:6px 0}");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine("<h1>Chat Run Report</h1>");
        sb.AppendLine("<div class='card'>");
        sb.AppendLine("<h2>Overview</h2>");
        sb.AppendLine("<table><tbody>");
        AppendRow(sb, "Workflow", report.WorkflowName);
        AppendRow(sb, "RunId", report.RunId);
        AppendRow(sb, "RootActor", report.RootActorId);
        AppendRow(sb, "Success", report.Success?.ToString() ?? "(unknown)");
        AppendRow(sb, "DurationMs", report.DurationMs.ToString("F2"));
        AppendRow(sb, "StartedAt", report.StartedAt.ToString("O"));
        AppendRow(sb, "EndedAt", report.EndedAt.ToString("O"));
        sb.AppendLine("</tbody></table>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class='card'><h2>Summary</h2><table><tbody>");
        AppendRow(sb, "TotalSteps", report.Summary.TotalSteps.ToString());
        AppendRow(sb, "RequestedSteps", report.Summary.RequestedSteps.ToString());
        AppendRow(sb, "CompletedSteps", report.Summary.CompletedSteps.ToString());
        AppendRow(sb, "RoleReplyCount", report.Summary.RoleReplyCount.ToString());
        foreach (var (stepType, count) in report.Summary.StepTypeCounts.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            AppendRow(sb, $"StepType.{stepType}", count.ToString());
        sb.AppendLine("</tbody></table></div>");

        sb.AppendLine("<div class='card'><h2>Input</h2>");
        sb.AppendLine($"<pre>{E(report.Input)}</pre>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class='card'><h2>Final Output</h2>");
        if (!string.IsNullOrWhiteSpace(report.FinalError))
            sb.AppendLine($"<div class='muted'>Error: {E(report.FinalError)}</div>");
        sb.AppendLine($"<pre>{E(report.FinalOutput)}</pre>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class='card'><h2>Topology</h2>");
        sb.AppendLine("<table><thead><tr><th>Parent</th><th>Child</th></tr></thead><tbody>");
        if (report.Topology.Count == 0)
            sb.AppendLine("<tr><td colspan='2' class='muted'>(no links)</td></tr>");
        else
        {
            foreach (var edge in report.Topology)
                sb.AppendLine($"<tr><td>{E(edge.Parent)}</td><td>{E(edge.Child)}</td></tr>");
        }
        sb.AppendLine("</tbody></table></div>");

        sb.AppendLine("<div class='card'><h2>Steps</h2>");
        sb.AppendLine("<table><thead><tr><th>StepId</th><th>Type</th><th>TargetRole</th><th>Success</th><th>Worker</th><th>DurationMs</th><th>OutputPreview</th><th>Error</th></tr></thead><tbody>");
        foreach (var step in report.Steps)
        {
            sb.AppendLine("<tr>");
            sb.AppendLine($"<td><code>{E(step.StepId)}</code></td>");
            sb.AppendLine($"<td>{E(step.StepType)}</td>");
            sb.AppendLine($"<td>{E(step.TargetRole)}</td>");
            sb.AppendLine($"<td>{E(step.Success?.ToString() ?? "")}</td>");
            sb.AppendLine($"<td>{E(step.WorkerId)}</td>");
            sb.AppendLine($"<td>{(step.DurationMs.HasValue ? step.DurationMs.Value.ToString("F2") : "")}</td>");
            sb.AppendLine($"<td>{E(step.OutputPreview)}</td>");
            sb.AppendLine($"<td>{E(step.Error)}</td>");
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table></div>");

        sb.AppendLine("<div class='card'><h2>Role Replies</h2>");
        if (report.RoleReplies.Count == 0)
            sb.AppendLine("<div class='muted'>(no role replies captured)</div>");
        else
        {
            foreach (var reply in report.RoleReplies)
            {
                sb.AppendLine("<details>");
                sb.AppendLine($"<summary><code>{E(reply.RoleId)}</code> session={E(reply.SessionId)} chars={reply.ContentLength} time={E(reply.Timestamp.ToString("HH:mm:ss.fff"))}</summary>");
                sb.AppendLine($"<pre>{E(reply.Content)}</pre>");
                sb.AppendLine("</details>");
            }
        }
        sb.AppendLine("</div>");

        sb.AppendLine("<div class='card'><h2>Timeline</h2>");
        sb.AppendLine("<table><thead><tr><th>Time</th><th>Stage</th><th>Agent</th><th>StepId</th><th>StepType</th><th>Message</th></tr></thead><tbody>");
        foreach (var evt in report.Timeline)
        {
            sb.AppendLine("<tr>");
            sb.AppendLine($"<td>{E(evt.Timestamp.ToString("HH:mm:ss.fff"))}</td>");
            sb.AppendLine($"<td>{E(evt.Stage)}</td>");
            sb.AppendLine($"<td>{E(evt.AgentId)}</td>");
            sb.AppendLine($"<td>{E(evt.StepId)}</td>");
            sb.AppendLine($"<td>{E(evt.StepType)}</td>");
            sb.AppendLine($"<td>{E(evt.Message)}</td>");
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table></div>");

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, string key, string value) =>
        sb.AppendLine($"<tr><td><code>{E(key)}</code></td><td>{E(value)}</td></tr>");

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
}
