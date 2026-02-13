using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar;
using Aevatar.AI;
using Aevatar.Cognitive;

internal sealed class MakerRunRecorder
{
    private readonly object _lock = new();
    private readonly string _rootActorId;
    private readonly Dictionary<string, MakerStepTrace> _steps = new(StringComparer.Ordinal);
    private readonly List<MakerRoleReply> _roleReplies = [];
    private readonly List<MakerTimelineEvent> _timeline = [];
    private string _runId = string.Empty;
    private bool? _success;
    private string _finalOutput = string.Empty;
    private string _finalError = string.Empty;

    public MakerRunRecorder(string rootActorId)
    {
        _rootActorId = rootActorId;
    }

    public void RecordEnvelope(EventEnvelope envelope)
    {
        if (envelope.Payload == null) return;
        var typeUrl = envelope.Payload.TypeUrl ?? "";
        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            if (typeUrl.Contains("StartWorkflowEvent"))
            {
                var evt = envelope.Payload.Unpack<StartWorkflowEvent>();
                _runId = string.IsNullOrWhiteSpace(_runId) ? evt.RunId : _runId;
                AddTimeline(now, "workflow.start", $"run={evt.RunId}", envelope.PublisherId, null, null, typeUrl);
                return;
            }

            if (typeUrl.Contains("StepRequestEvent"))
            {
                var evt = envelope.Payload.Unpack<StepRequestEvent>();
                _runId = string.IsNullOrWhiteSpace(_runId) ? evt.RunId : _runId;

                var step = GetOrCreateStep(evt.StepId);
                step.StepType = evt.StepType;
                step.RunId = evt.RunId;
                step.TargetRole = evt.TargetRole;
                step.RequestedAt = now;
                step.RequestParameters = evt.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value);

                AddTimeline(
                    now,
                    "step.request",
                    $"{evt.StepId} ({evt.StepType})",
                    envelope.PublisherId,
                    evt.StepId,
                    evt.StepType,
                    typeUrl,
                    evt.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value));
                return;
            }

            if (typeUrl.Contains("StepCompletedEvent"))
            {
                var evt = envelope.Payload.Unpack<StepCompletedEvent>();
                _runId = string.IsNullOrWhiteSpace(_runId) ? evt.RunId : _runId;

                var step = GetOrCreateStep(evt.StepId);
                if (string.IsNullOrWhiteSpace(step.RunId)) step.RunId = evt.RunId;
                step.CompletedAt = now;
                step.Success = evt.Success;
                step.Error = evt.Error ?? "";
                step.OutputPreview = Truncate(evt.Output ?? "", 240);
                step.WorkerId = evt.WorkerId ?? "";
                step.CompletionMetadata = evt.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value);

                AddTimeline(
                    now,
                    "step.completed",
                    $"{evt.StepId} success={evt.Success}",
                    envelope.PublisherId,
                    evt.StepId,
                    step.StepType,
                    typeUrl,
                    evt.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value));

                if (step.StepType.Equals("maker_vote", StringComparison.OrdinalIgnoreCase))
                {
                    var flagged = evt.Metadata.TryGetValue("maker_vote.red_flagged", out var f) ? f : "0";
                    var total = evt.Metadata.TryGetValue("maker_vote.total_candidates", out var t) ? t : "0";
                    AddTimeline(
                        now,
                        "maker.red_flag",
                        $"{evt.StepId} red_flagged={flagged}/{total}",
                        envelope.PublisherId,
                        evt.StepId,
                        step.StepType,
                        typeUrl);
                    AddTimeline(
                        now,
                        "maker.vote",
                        $"{evt.StepId} voted success={evt.Success}",
                        envelope.PublisherId,
                        evt.StepId,
                        step.StepType,
                        typeUrl);
                }

                if (step.StepType.Equals("connector_call", StringComparison.OrdinalIgnoreCase))
                {
                    var connectorName = evt.Metadata.TryGetValue("connector.name", out var cName) ? cName : "";
                    var connectorType = evt.Metadata.TryGetValue("connector.type", out var cType) ? cType : "";
                    var duration = evt.Metadata.TryGetValue("connector.duration_ms", out var cDur) ? cDur : "";
                    var statusCode = evt.Metadata.TryGetValue("connector.http.status_code", out var code) ? code : "";
                    var exitCode = evt.Metadata.TryGetValue("connector.cli.exit_code", out var exit) ? exit : "";
                    var redFlag = evt.Metadata.TryGetValue("red_flag", out var rf) ? rf : "";

                    var connectorData = new Dictionary<string, string>
                    {
                        ["connector.name"] = connectorName,
                        ["connector.type"] = connectorType,
                        ["duration_ms"] = duration,
                    };
                    if (!string.IsNullOrWhiteSpace(statusCode)) connectorData["status_code"] = statusCode;
                    if (!string.IsNullOrWhiteSpace(exitCode)) connectorData["exit_code"] = exitCode;
                    if (!string.IsNullOrWhiteSpace(redFlag)) connectorData["red_flag"] = redFlag;

                    AddTimeline(
                        now,
                        "connector.call",
                        $"{evt.StepId} connector={connectorName} type={connectorType}",
                        envelope.PublisherId,
                        evt.StepId,
                        step.StepType,
                        typeUrl,
                        connectorData);
                }

                if (step.StepType.Equals("maker_recursive", StringComparison.OrdinalIgnoreCase))
                {
                    var stage = evt.Metadata.TryGetValue("maker.stage", out var st) ? st : "";
                    var depth = evt.Metadata.TryGetValue("maker.depth", out var dp) ? dp : "";
                    var atomic = evt.Metadata.TryGetValue("maker.atomic_decision", out var at) ? at : "";
                    AddTimeline(
                        now,
                        "maker.recursive",
                        $"{evt.StepId} stage={stage} depth={depth} atomic={atomic}",
                        envelope.PublisherId,
                        evt.StepId,
                        step.StepType,
                        typeUrl,
                        new Dictionary<string, string>
                        {
                            ["stage"] = stage,
                            ["depth"] = depth,
                            ["atomic"] = atomic,
                        });
                }

                return;
            }

            if (typeUrl.Contains("TextMessageStartEvent"))
            {
                var evt = envelope.Payload.Unpack<TextMessageStartEvent>();
                AddTimeline(
                    now,
                    "llm.start",
                    $"agent={evt.AgentId}, session={evt.SessionId}",
                    envelope.PublisherId,
                    null,
                    null,
                    typeUrl,
                    new Dictionary<string, string> { ["session_id"] = evt.SessionId, ["agent_id"] = evt.AgentId });
                return;
            }

            if (typeUrl.Contains("TextMessageEndEvent"))
            {
                var evt = envelope.Payload.Unpack<TextMessageEndEvent>();
                var publisher = string.IsNullOrWhiteSpace(envelope.PublisherId) ? "(unknown)" : envelope.PublisherId;

                if (!string.Equals(publisher, _rootActorId, StringComparison.Ordinal))
                {
                    _roleReplies.Add(new MakerRoleReply
                    {
                        Timestamp = now,
                        RoleId = publisher,
                        SessionId = evt.SessionId ?? "",
                        Content = evt.Content ?? "",
                        ContentLength = (evt.Content ?? "").Length,
                    });
                }

                AddTimeline(
                    now,
                    "llm.end",
                    $"agent={publisher}, chars={(evt.Content ?? "").Length}",
                    publisher,
                    null,
                    null,
                    typeUrl,
                    new Dictionary<string, string> { ["session_id"] = evt.SessionId ?? "" });
                return;
            }

            if (typeUrl.Contains("WorkflowCompletedEvent"))
            {
                var evt = envelope.Payload.Unpack<WorkflowCompletedEvent>();
                _runId = string.IsNullOrWhiteSpace(_runId) ? evt.RunId : _runId;
                _success = evt.Success;
                _finalOutput = evt.Output ?? "";
                _finalError = evt.Error ?? "";

                AddTimeline(
                    now,
                    "workflow.completed",
                    $"success={evt.Success}",
                    envelope.PublisherId,
                    null,
                    null,
                    typeUrl,
                    new Dictionary<string, string> { ["workflow_name"] = evt.WorkflowName, ["run_id"] = evt.RunId });
            }
        }
    }

    public MakerRunReport BuildReport(
        string workflowName,
        string workflowPath,
        string providerName,
        string modelName,
        string inputText,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        bool timedOut,
        List<MakerTopologyEdge> topology)
    {
        lock (_lock)
        {
            var steps = _steps.Values
                .OrderBy(x => x.RequestedAt ?? x.CompletedAt ?? DateTimeOffset.MinValue)
                .ToList();
            var timeline = _timeline.OrderBy(x => x.Timestamp).ToList();
            var roleReplies = _roleReplies.OrderBy(x => x.Timestamp).ToList();

            var stepTypeCounts = steps
                .Where(x => !string.IsNullOrWhiteSpace(x.StepType))
                .GroupBy(x => x.StepType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var connectorTypeCounts = steps
                .Where(x => string.Equals(x.StepType, "connector_call", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.CompletionMetadata.TryGetValue("connector.type", out var t) ? t : "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var totalRedFlagged = steps
                .Where(x => string.Equals(x.StepType, "maker_vote", StringComparison.OrdinalIgnoreCase))
                .Select(x => TryParseInt(x.CompletionMetadata.GetValueOrDefault("maker_vote.red_flagged")))
                .Sum();

            var report = new MakerRunReport
            {
                ReportVersion = "1.0",
                WorkflowName = workflowName,
                WorkflowPath = workflowPath,
                RootActorId = _rootActorId,
                RunId = _runId,
                Provider = providerName,
                Model = modelName,
                StartedAt = startedAt,
                EndedAt = endedAt,
                DurationMs = Math.Max(0, (endedAt - startedAt).TotalMilliseconds),
                TimedOut = timedOut,
                Success = timedOut ? false : _success,
                Input = inputText,
                FinalOutput = _finalOutput,
                FinalError = _finalError,
                Topology = topology,
                Steps = steps,
                RoleReplies = roleReplies,
                Timeline = timeline,
                Summary = new MakerRunSummary
                {
                    TotalSteps = steps.Count,
                    RequestedSteps = steps.Count(x => x.RequestedAt != null),
                    CompletedSteps = steps.Count(x => x.CompletedAt != null),
                    VoteSteps = steps.Count(x => string.Equals(x.StepType, "maker_vote", StringComparison.OrdinalIgnoreCase)),
                    ConnectorSteps = steps.Count(x => string.Equals(x.StepType, "connector_call", StringComparison.OrdinalIgnoreCase)),
                    TotalRedFlaggedCandidates = totalRedFlagged,
                    RoleReplyCount = roleReplies.Count,
                    StepTypeCounts = stepTypeCounts,
                    ConnectorTypeCounts = connectorTypeCounts,
                },
            };

            return report;
        }
    }

    private MakerStepTrace GetOrCreateStep(string stepId)
    {
        if (_steps.TryGetValue(stepId, out var step))
            return step;

        step = new MakerStepTrace { StepId = stepId };
        _steps[stepId] = step;
        return step;
    }

    private void AddTimeline(
        DateTimeOffset timestamp,
        string stage,
        string message,
        string? agentId,
        string? stepId,
        string? stepType,
        string eventType,
        Dictionary<string, string>? data = null)
    {
        _timeline.Add(new MakerTimelineEvent
        {
            Timestamp = timestamp,
            Stage = stage,
            Message = message,
            AgentId = agentId ?? "",
            StepId = stepId ?? "",
            StepType = stepType ?? "",
            EventType = eventType,
            Data = data ?? [],
        });
    }

    private static string Truncate(string text, int maxLen)
    {
        if (text.Length <= maxLen) return text;
        return text[..maxLen] + "...";
    }

    private static int TryParseInt(string? value) =>
        int.TryParse(value, out var n) ? n : 0;
}

internal static class MakerRunReportWriter
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
            Path.Combine(outputDirectory, $"maker-run-{stamp}.json"),
            Path.Combine(outputDirectory, $"maker-run-{stamp}.html"));
    }

    public static async Task WriteAsync(MakerRunReport report, string jsonPath, string htmlPath)
    {
        EnsureParentDirectory(jsonPath);
        EnsureParentDirectory(htmlPath);

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(jsonPath, json);
        await File.WriteAllTextAsync(htmlPath, BuildHtml(report));
    }

    private static string BuildHtml(MakerRunReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html><head><meta charset=\"utf-8\"/>");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>");
        sb.AppendLine("<title>MAKER Execution Report</title>");
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

        sb.AppendLine("<h1>MAKER Execution Report</h1>");
        sb.AppendLine("<div class='card'>");
        sb.AppendLine("<h2>Overview</h2>");
        sb.AppendLine("<table><tbody>");
        AppendRow(sb, "Workflow", report.WorkflowName);
        AppendRow(sb, "RunId", report.RunId);
        AppendRow(sb, "RootActor", report.RootActorId);
        AppendRow(sb, "Provider/Model", $"{report.Provider} / {report.Model}");
        AppendRow(sb, "Success", report.Success?.ToString() ?? "(unknown)");
        AppendRow(sb, "TimedOut", report.TimedOut.ToString());
        AppendRow(sb, "DurationMs", report.DurationMs.ToString("F2"));
        AppendRow(sb, "StartedAt", report.StartedAt.ToString("O"));
        AppendRow(sb, "EndedAt", report.EndedAt.ToString("O"));
        AppendRow(sb, "WorkflowPath", report.WorkflowPath);
        sb.AppendLine("</tbody></table>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class='card'><h2>Summary</h2><table><tbody>");
        AppendRow(sb, "TotalSteps", report.Summary.TotalSteps.ToString());
        AppendRow(sb, "RequestedSteps", report.Summary.RequestedSteps.ToString());
        AppendRow(sb, "CompletedSteps", report.Summary.CompletedSteps.ToString());
        AppendRow(sb, "VoteSteps", report.Summary.VoteSteps.ToString());
        AppendRow(sb, "ConnectorSteps", report.Summary.ConnectorSteps.ToString());
        AppendRow(sb, "TotalRedFlaggedCandidates", report.Summary.TotalRedFlaggedCandidates.ToString());
        AppendRow(sb, "RoleReplyCount", report.Summary.RoleReplyCount.ToString());
        foreach (var (stepType, count) in report.Summary.StepTypeCounts.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            AppendRow(sb, $"StepType.{stepType}", count.ToString());
        foreach (var (connectorType, count) in report.Summary.ConnectorTypeCounts.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            AppendRow(sb, $"ConnectorType.{connectorType}", count.ToString());
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
        {
            sb.AppendLine("<tr><td colspan='2' class='muted'>(no links)</td></tr>");
        }
        else
        {
            foreach (var edge in report.Topology)
                sb.AppendLine($"<tr><td>{E(edge.Parent)}</td><td>{E(edge.Child)}</td></tr>");
        }
        sb.AppendLine("</tbody></table></div>");

        sb.AppendLine("<div class='card'><h2>Steps</h2>");
        sb.AppendLine("<table><thead><tr><th>StepId</th><th>Type</th><th>TargetRole</th><th>Success</th><th>Worker</th><th>DurationMs</th><th>Metadata</th><th>OutputPreview</th><th>Error</th></tr></thead><tbody>");
        foreach (var step in report.Steps)
        {
            var metadata = step.CompletionMetadata.Count == 0
                ? ""
                : string.Join("<br/>", step.CompletionMetadata.OrderBy(k => k.Key, StringComparer.Ordinal)
                    .Select(kv => $"{E(kv.Key)}={E(kv.Value)}"));
            sb.AppendLine("<tr>");
            sb.AppendLine($"<td><code>{E(step.StepId)}</code></td>");
            sb.AppendLine($"<td>{E(step.StepType)}</td>");
            sb.AppendLine($"<td>{E(step.TargetRole)}</td>");
            sb.AppendLine($"<td>{E(step.Success?.ToString() ?? "")}</td>");
            sb.AppendLine($"<td>{E(step.WorkerId)}</td>");
            sb.AppendLine($"<td>{(step.DurationMs.HasValue ? step.DurationMs.Value.ToString("F2") : "")}</td>");
            sb.AppendLine($"<td>{metadata}</td>");
            sb.AppendLine($"<td>{E(step.OutputPreview)}</td>");
            sb.AppendLine($"<td>{E(step.Error)}</td>");
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table></div>");

        sb.AppendLine("<div class='card'><h2>Role Replies</h2>");
        if (report.RoleReplies.Count == 0)
        {
            sb.AppendLine("<div class='muted'>(no role replies captured)</div>");
        }
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

    private static void AppendRow(StringBuilder sb, string key, string value)
    {
        sb.AppendLine($"<tr><td><code>{E(key)}</code></td><td>{E(value)}</td></tr>");
    }

    private static string E(string text) => WebUtility.HtmlEncode(text ?? "");

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }
}

internal sealed class MakerRunReport
{
    public string ReportVersion { get; set; } = "1.0";
    public string WorkflowName { get; set; } = "";
    public string WorkflowPath { get; set; } = "";
    public string RootActorId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public double DurationMs { get; set; }
    public bool TimedOut { get; set; }
    public bool? Success { get; set; }
    public string Input { get; set; } = "";
    public string FinalOutput { get; set; } = "";
    public string FinalError { get; set; } = "";
    public List<MakerTopologyEdge> Topology { get; set; } = [];
    public List<MakerStepTrace> Steps { get; set; } = [];
    public List<MakerRoleReply> RoleReplies { get; set; } = [];
    public List<MakerTimelineEvent> Timeline { get; set; } = [];
    public MakerRunSummary Summary { get; set; } = new();
}

internal sealed class MakerRunSummary
{
    public int TotalSteps { get; set; }
    public int RequestedSteps { get; set; }
    public int CompletedSteps { get; set; }
    public int VoteSteps { get; set; }
    public int ConnectorSteps { get; set; }
    public int TotalRedFlaggedCandidates { get; set; }
    public int RoleReplyCount { get; set; }
    public Dictionary<string, int> StepTypeCounts { get; set; } = [];
    public Dictionary<string, int> ConnectorTypeCounts { get; set; } = [];
}

internal sealed class MakerStepTrace
{
    public string StepId { get; set; } = "";
    public string StepType { get; set; } = "";
    public string RunId { get; set; } = "";
    public string TargetRole { get; set; } = "";
    public DateTimeOffset? RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool? Success { get; set; }
    public string WorkerId { get; set; } = "";
    public string OutputPreview { get; set; } = "";
    public string Error { get; set; } = "";
    public Dictionary<string, string> RequestParameters { get; set; } = [];
    public Dictionary<string, string> CompletionMetadata { get; set; } = [];

    public double? DurationMs =>
        RequestedAt.HasValue && CompletedAt.HasValue
            ? Math.Max(0, (CompletedAt.Value - RequestedAt.Value).TotalMilliseconds)
            : null;
}

internal sealed class MakerRoleReply
{
    public DateTimeOffset Timestamp { get; set; }
    public string RoleId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Content { get; set; } = "";
    public int ContentLength { get; set; }
}

internal sealed class MakerTimelineEvent
{
    public DateTimeOffset Timestamp { get; set; }
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string StepId { get; set; } = "";
    public string StepType { get; set; } = "";
    public string EventType { get; set; } = "";
    public Dictionary<string, string> Data { get; set; } = [];
}

internal sealed record MakerTopologyEdge(string Parent, string Child);
