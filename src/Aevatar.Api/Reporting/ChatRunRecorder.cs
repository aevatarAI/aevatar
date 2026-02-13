// ─────────────────────────────────────────────────────────────
// ChatRunRecorder — 订阅 WorkflowGAgent stream，记录事件并生成通用执行报告
// 不依赖 maker 专有类型，供 Api Chat 端点使用
// ─────────────────────────────────────────────────────────────

using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar;
using Aevatar.AI;
using Aevatar.Cognitive;

namespace Aevatar.Api.Reporting;

/// <summary>Records EventEnvelope from agent stream and builds a generic run report.</summary>
public sealed class ChatRunRecorder
{
    private readonly object _lock = new();
    private readonly string _rootActorId;
    private readonly Dictionary<string, ChatStepTrace> _steps = new(StringComparer.Ordinal);
    private readonly List<ChatRoleReply> _roleReplies = [];
    private readonly List<ChatTimelineEvent> _timeline = [];
    private string _runId = string.Empty;
    private bool? _success;
    private string _finalOutput = string.Empty;
    private string _finalError = string.Empty;
    private DateTimeOffset? _endedAt;

    public ChatRunRecorder(string rootActorId)
    {
        _rootActorId = rootActorId;
    }

    public void Record(EventEnvelope envelope)
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
                AddTimeline(now, "step.request", $"{evt.StepId} ({evt.StepType})", envelope.PublisherId, evt.StepId, evt.StepType, typeUrl, evt.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value));
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
                AddTimeline(now, "step.completed", $"{evt.StepId} success={evt.Success}", envelope.PublisherId, evt.StepId, step.StepType, typeUrl, evt.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value));
                return;
            }

            if (typeUrl.Contains("TextMessageEndEvent"))
            {
                var evt = envelope.Payload.Unpack<TextMessageEndEvent>();
                var publisher = string.IsNullOrWhiteSpace(envelope.PublisherId) ? "(unknown)" : envelope.PublisherId;
                if (!string.Equals(publisher, _rootActorId, StringComparison.Ordinal))
                {
                    _roleReplies.Add(new ChatRoleReply
                    {
                        Timestamp = now,
                        RoleId = publisher,
                        SessionId = evt.SessionId ?? "",
                        Content = evt.Content ?? "",
                        ContentLength = (evt.Content ?? "").Length,
                    });
                }
                AddTimeline(now, "llm.end", $"agent={publisher}, chars={(evt.Content ?? "").Length}", publisher, null, null, typeUrl, new Dictionary<string, string> { ["session_id"] = evt.SessionId ?? "" });
                return;
            }

            if (typeUrl.Contains("WorkflowCompletedEvent"))
            {
                var evt = envelope.Payload.Unpack<WorkflowCompletedEvent>();
                _runId = string.IsNullOrWhiteSpace(_runId) ? evt.RunId : _runId;
                _success = evt.Success;
                _finalOutput = evt.Output ?? "";
                _finalError = evt.Error ?? "";
                _endedAt = now;
                AddTimeline(now, "workflow.completed", $"success={evt.Success}", envelope.PublisherId, null, null, typeUrl, new Dictionary<string, string> { ["workflow_name"] = evt.WorkflowName, ["run_id"] = evt.RunId });
            }
        }
    }

    public ChatRunReport BuildReport(
        string workflowName,
        string runId,
        DateTimeOffset startedAt,
        string inputText,
        List<ChatTopologyEdge> topology)
    {
        lock (_lock)
        {
            var steps = _steps.Values.OrderBy(x => x.RequestedAt ?? x.CompletedAt ?? DateTimeOffset.MinValue).ToList();
            var timeline = _timeline.OrderBy(x => x.Timestamp).ToList();
            var stepTypeCounts = steps.Where(x => !string.IsNullOrWhiteSpace(x.StepType)).GroupBy(x => x.StepType!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var endedAt = _endedAt ?? DateTimeOffset.UtcNow;
            var durationMs = (endedAt - startedAt).TotalMilliseconds;

            return new ChatRunReport
            {
                ReportVersion = "1.0",
                WorkflowName = workflowName,
                RootActorId = _rootActorId,
                RunId = runId,
                StartedAt = startedAt,
                EndedAt = endedAt,
                DurationMs = Math.Max(0, durationMs),
                Success = _success,
                Input = inputText ?? "",
                FinalOutput = _finalOutput,
                FinalError = _finalError,
                Topology = topology ?? [],
                Steps = steps,
                RoleReplies = _roleReplies.OrderBy(x => x.Timestamp).ToList(),
                Timeline = timeline,
                Summary = new ChatRunSummary
                {
                    TotalSteps = steps.Count,
                    RequestedSteps = steps.Count(x => x.RequestedAt != null),
                    CompletedSteps = steps.Count(x => x.CompletedAt != null),
                    RoleReplyCount = _roleReplies.Count,
                    StepTypeCounts = stepTypeCounts,
                },
            };
        }
    }

    private ChatStepTrace GetOrCreateStep(string stepId)
    {
        if (_steps.TryGetValue(stepId, out var step)) return step;
        step = new ChatStepTrace { StepId = stepId };
        _steps[stepId] = step;
        return step;
    }

    private void AddTimeline(DateTimeOffset timestamp, string stage, string message, string? agentId, string? stepId, string? stepType, string eventType, Dictionary<string, string>? data = null)
    {
        _timeline.Add(new ChatTimelineEvent
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

    private static string Truncate(string text, int maxLen) => text.Length <= maxLen ? text : text[..maxLen] + "...";
}

public sealed class ChatRunReport
{
    public string ReportVersion { get; set; } = "1.0";
    public string WorkflowName { get; set; } = "";
    public string RootActorId { get; set; } = "";
    public string RunId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public double DurationMs { get; set; }
    public bool? Success { get; set; }
    public string Input { get; set; } = "";
    public string FinalOutput { get; set; } = "";
    public string FinalError { get; set; } = "";
    public List<ChatTopologyEdge> Topology { get; set; } = [];
    public List<ChatStepTrace> Steps { get; set; } = [];
    public List<ChatRoleReply> RoleReplies { get; set; } = [];
    public List<ChatTimelineEvent> Timeline { get; set; } = [];
    public ChatRunSummary Summary { get; set; } = new();
}

public sealed class ChatRunSummary
{
    public int TotalSteps { get; set; }
    public int RequestedSteps { get; set; }
    public int CompletedSteps { get; set; }
    public int RoleReplyCount { get; set; }
    public Dictionary<string, int> StepTypeCounts { get; set; } = [];
}

public sealed class ChatStepTrace
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
    public double? DurationMs => RequestedAt.HasValue && CompletedAt.HasValue ? Math.Max(0, (CompletedAt.Value - RequestedAt.Value).TotalMilliseconds) : null;
}

public sealed class ChatRoleReply
{
    public DateTimeOffset Timestamp { get; set; }
    public string RoleId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Content { get; set; } = "";
    public int ContentLength { get; set; }
}

public sealed class ChatTimelineEvent
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

public sealed record ChatTopologyEdge(string Parent, string Child);
