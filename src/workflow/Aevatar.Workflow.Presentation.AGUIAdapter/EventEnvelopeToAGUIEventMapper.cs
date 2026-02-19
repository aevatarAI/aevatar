using Aevatar.Presentation.AGUI;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core;
using AIEvents = Aevatar.AI.Abstractions;
using AGUI = Aevatar.Presentation.AGUI;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public interface IEventEnvelopeToAGUIEventMapper
{
    IReadOnlyList<AGUIEvent> Map(EventEnvelope envelope);
}

public interface IAGUIEventEnvelopeMappingHandler
{
    int Order { get; }

    bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events);
}

public sealed class EventEnvelopeToAGUIEventMapper : IEventEnvelopeToAGUIEventMapper
{
    private readonly IReadOnlyList<IAGUIEventEnvelopeMappingHandler> _handlers;

    public EventEnvelopeToAGUIEventMapper(IEnumerable<IAGUIEventEnvelopeMappingHandler> handlers)
    {
        _handlers = handlers.OrderBy(x => x.Order).ToList();
    }

    public IReadOnlyList<AGUIEvent> Map(EventEnvelope envelope)
    {
        if (envelope.Payload == null)
            return [];

        var output = new List<AGUIEvent>();
        foreach (var handler in _handlers)
        {
            if (!handler.TryMap(envelope, out var mapped) || mapped.Count == 0)
                continue;

            output.AddRange(mapped);
        }

        return output;
    }
}

public sealed class StartWorkflowAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 0;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        if (envelope.Payload?.Is(StartWorkflowEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<StartWorkflowEvent>();
        events =
        [
            new RunStartedEvent
            {
                Timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp),
                ThreadId = AGUIEventEnvelopeMappingHelpers.ResolveThreadId(envelope, evt.WorkflowName),
                RunId = evt.RunId,
            },
        ];
        return true;
    }
}

public sealed class StepRequestAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 10;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        if (envelope.Payload?.Is(StepRequestEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<StepRequestEvent>();
        var ts = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp);
        events =
        [
            new StepStartedEvent { Timestamp = ts, StepName = evt.StepId },
            new CustomEvent
            {
                Timestamp = ts,
                Name = "aevatar.step.request",
                Value = new { evt.StepId, evt.StepType, evt.TargetRole, evt.RunId },
            },
        ];
        return true;
    }
}

public sealed class StepCompletedAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 20;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        if (envelope.Payload?.Is(StepCompletedEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<StepCompletedEvent>();
        events =
        [
            new StepFinishedEvent
            {
                Timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp),
                StepName = evt.StepId,
            },
        ];
        return true;
    }
}

public sealed class AITextStreamAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 30;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        events = [];
        if (envelope.Payload == null)
            return false;

        var payload = envelope.Payload;
        var ts = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp);

        if (payload.Is(AIEvents.TextMessageStartEvent.Descriptor))
        {
            var evt = payload.Unpack<AIEvents.TextMessageStartEvent>();
            var msgId = AGUIEventEnvelopeMappingHelpers.ResolveMessageId(evt.SessionId, envelope.Id);
            events =
            [
                new AGUI.TextMessageStartEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                    Role = "assistant",
                },
            ];
            return true;
        }

        if (payload.Is(AIEvents.TextMessageContentEvent.Descriptor))
        {
            var evt = payload.Unpack<AIEvents.TextMessageContentEvent>();
            var msgId = AGUIEventEnvelopeMappingHelpers.ResolveMessageId(evt.SessionId, envelope.Id);
            events =
            [
                new AGUI.TextMessageContentEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                    Delta = evt.Delta,
                },
            ];
            return true;
        }

        if (payload.Is(AIEvents.TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<AIEvents.TextMessageEndEvent>();
            var msgId = AGUIEventEnvelopeMappingHelpers.ResolveMessageId(evt.SessionId, envelope.Id);
            events =
            [
                new AGUI.TextMessageEndEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                },
            ];
            return true;
        }

        if (payload.Is(AIEvents.ChatResponseEvent.Descriptor))
        {
            var evt = payload.Unpack<AIEvents.ChatResponseEvent>();
            var msgId = AGUIEventEnvelopeMappingHelpers.ResolveMessageId(evt.SessionId, envelope.Id);
            events =
            [
                new AGUI.TextMessageStartEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                    Role = "assistant",
                },
                new AGUI.TextMessageContentEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                    Delta = evt.Content,
                },
                new AGUI.TextMessageEndEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                },
            ];
            return true;
        }

        return false;
    }
}

public sealed class WorkflowCompletedAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 40;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        if (envelope.Payload?.Is(WorkflowCompletedEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<WorkflowCompletedEvent>();
        var ts = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp);

        if (evt.Success)
        {
            events =
            [
                new RunFinishedEvent
                {
                    Timestamp = ts,
                    ThreadId = AGUIEventEnvelopeMappingHelpers.ResolveThreadId(envelope, evt.WorkflowName),
                    RunId = evt.RunId,
                    Result = new { output = evt.Output },
                },
            ];
            return true;
        }

        events =
        [
            new RunErrorEvent
            {
                Timestamp = ts,
                Message = evt.Error,
                RunId = evt.RunId,
                Code = "WORKFLOW_FAILED",
            },
        ];
        return true;
    }
}

public sealed class ToolCallAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 50;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        events = [];
        if (envelope.Payload == null)
            return false;

        var payload = envelope.Payload;
        var ts = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp);

        if (payload.Is(AIEvents.ToolCallEvent.Descriptor))
        {
            var evt = payload.Unpack<AIEvents.ToolCallEvent>();
            events =
            [
                new ToolCallStartEvent
                {
                    Timestamp = ts,
                    ToolCallId = evt.CallId,
                    ToolName = evt.ToolName,
                },
            ];
            return true;
        }

        if (payload.Is(AIEvents.ToolResultEvent.Descriptor))
        {
            var evt = payload.Unpack<AIEvents.ToolResultEvent>();
            events =
            [
                new ToolCallEndEvent
                {
                    Timestamp = ts,
                    ToolCallId = evt.CallId,
                    Result = evt.ResultJson,
                },
            ];
            return true;
        }

        return false;
    }
}

public sealed class WorkflowSuspendedAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 45;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        if (envelope.Payload?.Is(WorkflowSuspendedEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<WorkflowSuspendedEvent>();
        var ts = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp);

        var metadata = new Dictionary<string, string>();
        foreach (var (key, value) in evt.Metadata)
            metadata[key] = value;

        events =
        [
            new HumanInputRequestEvent
            {
                Timestamp = ts,
                StepId = evt.StepId,
                RunId = evt.RunId,
                SuspensionType = evt.SuspensionType,
                Prompt = evt.Prompt,
                TimeoutSeconds = evt.TimeoutSeconds,
                Metadata = metadata,
            },
        ];
        return true;
    }
}

internal static class AGUIEventEnvelopeMappingHelpers
{
    public static long? ToUnixMs(Timestamp? ts)
    {
        if (ts == null) return null;
        var dt = ts.ToDateTime();
        if (dt.Kind != DateTimeKind.Utc) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
    }

    public static string ResolveThreadId(EventEnvelope envelope, string fallback)
    {
        return string.IsNullOrWhiteSpace(envelope.PublisherId)
            ? fallback
            : envelope.PublisherId;
    }

    public static string ResolveMessageId(string? sessionId, string? envelopeId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
            return $"msg:{sessionId}";

        return $"msg:{envelopeId}";
    }
}
