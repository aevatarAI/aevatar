// ─────────────────────────────────────────────────────────────
// AevatarActivitySource - distributed tracing ActivitySource.
// Emits spans following OpenTelemetry GenAI Semantic Conventions:
//   invoke_agent, chat, execute_tool
// ─────────────────────────────────────────────────────────────

using System.Diagnostics;
using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Propagation;

namespace Aevatar.Foundation.Runtime.Observability;

/// <summary>ActivitySource for Aevatar agent distributed tracing (GenAI conventions).</summary>
public static class AevatarActivitySource
{
    private const string AgentIdTag = "aevatar.agent.id";
    private const string EventIdTag = "aevatar.event.id";
    private const string EventTypeTag = "aevatar.event.type";
    private const string EventDirectionTag = "aevatar.event.direction";
    private const string EventPublisherTag = "aevatar.event.publisher";

    /// <summary>ActivitySource instance.</summary>
    public static readonly ActivitySource Source = new("Aevatar.Agents", "1.0.0");

    /// <summary>Starts a HandleEvent activity (legacy, used by LocalActor).</summary>
    public static Activity? StartHandleEvent(string agentId, string eventId, string? eventTypeUrl = null)
    {
        var eventTypeName = ResolveEventTypeName(eventTypeUrl);
        var operationName = BuildHandleEventOperationName(eventTypeName);

        var activity = Source.StartActivity(operationName);
        if (activity == null)
            return null;

        activity.SetTag(AgentIdTag, agentId);
        activity.SetTag(EventIdTag, eventId);
        if (!string.IsNullOrWhiteSpace(eventTypeUrl))
            activity.SetTag(EventTypeTag, eventTypeUrl);
        else if (!string.IsNullOrWhiteSpace(eventTypeName))
            activity.SetTag(EventTypeTag, eventTypeName);

        return activity;
    }

    public static Activity? StartHandleEvent(string agentId, EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var eventTypeUrl = envelope.Payload?.TypeUrl;
        var eventTypeName = ResolveEventTypeName(eventTypeUrl);
        var operationName = BuildHandleEventOperationName(eventTypeName);

        var activity = TryStartWithEnvelopeParent(operationName, envelope) ?? Source.StartActivity(operationName);
        if (activity == null)
            return null;

        activity.SetTag(AgentIdTag, agentId);
        activity.SetTag(EventIdTag, envelope.Id);
        if (!string.IsNullOrWhiteSpace(eventTypeUrl))
            activity.SetTag(EventTypeTag, eventTypeUrl);
        else if (!string.IsNullOrWhiteSpace(eventTypeName))
            activity.SetTag(EventTypeTag, eventTypeName);
        activity.SetTag(EventDirectionTag, (envelope.Route?.Direction ?? EventDirection.Unspecified).ToString());
        if (!string.IsNullOrWhiteSpace(envelope.Route?.PublisherActorId))
            activity.SetTag(EventPublisherTag, envelope.Route.PublisherActorId);

        return activity;
    }

    private static string BuildHandleEventOperationName(string eventTypeName)
    {
        return string.IsNullOrWhiteSpace(eventTypeName)
            ? "HandleEvent:UnknownEvent"
            : $"HandleEvent:{eventTypeName}";
    }

    private static string ResolveEventTypeName(string? eventTypeUrl)
    {
        if (string.IsNullOrWhiteSpace(eventTypeUrl))
            return string.Empty;

        var separator = eventTypeUrl.LastIndexOf('/');
        var fullTypeName = separator < 0 || separator == eventTypeUrl.Length - 1
            ? eventTypeUrl
            : eventTypeUrl[(separator + 1)..];

        var dot = fullTypeName.LastIndexOf('.');
        if (dot < 0 || dot == fullTypeName.Length - 1)
            return fullTypeName;

        return fullTypeName[(dot + 1)..];
    }

    private static Activity? TryStartWithEnvelopeParent(string operationName, EventEnvelope envelope)
    {
        var trace = envelope.Propagation?.Trace;
        if (trace == null ||
            string.IsNullOrWhiteSpace(trace.TraceId) ||
            string.IsNullOrWhiteSpace(trace.SpanId))
        {
            return null;
        }

        try
        {
            var traceId = ActivityTraceId.CreateFromString(trace.TraceId.AsSpan());
            var spanId = ActivitySpanId.CreateFromString(trace.SpanId.AsSpan());
            var traceFlags = ResolveTraceFlags(trace.TraceFlags);
            var parent = new ActivityContext(traceId, spanId, traceFlags);
            return Source.StartActivity(operationName, ActivityKind.Internal, parent);
        }
        catch
        {
            return null;
        }
    }

    private static ActivityTraceFlags ResolveTraceFlags(string? flagsText)
    {
        if (string.IsNullOrWhiteSpace(flagsText))
        {
            return ActivityTraceFlags.None;
        }

        if (byte.TryParse(flagsText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flagsByte))
            return (ActivityTraceFlags)flagsByte;

        return Enum.TryParse<ActivityTraceFlags>(flagsText, ignoreCase: true, out var parsed)
            ? parsed
            : ActivityTraceFlags.None;
    }

    /// <summary>Starts an invoke_agent span per GenAI semantic conventions.</summary>
    public static Activity? StartInvokeAgent(string agentId, string? agentName = null, string? system = null)
    {
        var activity = Source.StartActivity($"invoke_agent {agentName ?? agentId}", ActivityKind.Client);
        if (activity == null) return null;

        activity.SetTag("gen_ai.operation.name", "invoke_agent");
        activity.SetTag("gen_ai.agent.id", agentId);
        if (agentName != null) activity.SetTag("gen_ai.agent.name", agentName);
        if (system != null) activity.SetTag("gen_ai.system", system);

        return activity;
    }

    /// <summary>Starts a chat span per GenAI semantic conventions.</summary>
    public static Activity? StartChat(string? model = null, string? system = null)
    {
        var activity = Source.StartActivity($"chat {model ?? "unknown"}", ActivityKind.Client);
        if (activity == null) return null;

        activity.SetTag("gen_ai.operation.name", "chat");
        if (model != null) activity.SetTag("gen_ai.request.model", model);
        if (system != null) activity.SetTag("gen_ai.system", system);

        return activity;
    }

    /// <summary>Records token usage on a chat span.</summary>
    public static void RecordTokenUsage(Activity? activity, int? inputTokens, int? outputTokens)
    {
        if (activity == null) return;
        if (inputTokens.HasValue) activity.SetTag("gen_ai.usage.input_tokens", inputTokens.Value);
        if (outputTokens.HasValue) activity.SetTag("gen_ai.usage.output_tokens", outputTokens.Value);
    }

    /// <summary>Starts an execute_tool span per GenAI semantic conventions.</summary>
    public static Activity? StartExecuteTool(string toolName, string? toolCallId = null)
    {
        var activity = Source.StartActivity($"execute_tool {toolName}", ActivityKind.Client);
        if (activity == null) return null;

        activity.SetTag("gen_ai.operation.name", "execute_tool");
        activity.SetTag("gen_ai.tool.name", toolName);
        if (toolCallId != null) activity.SetTag("gen_ai.tool.call_id", toolCallId);

        return activity;
    }

}
