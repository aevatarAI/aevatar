using Aevatar.Presentation.AGUI;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core;
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
        var threadId = AGUIEventEnvelopeMappingHelpers.ResolveThreadId(envelope, evt.WorkflowName);
        var runId = AGUIEventEnvelopeMappingHelpers.ResolveRunId(envelope, threadId);
        events =
        [
            new RunStartedEvent
            {
                Timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp),
                ThreadId = threadId,
                RunId = runId,
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
                Value = new
                {
                    evt.RunId,
                    evt.StepId,
                    evt.StepType,
                    evt.TargetRole,
                    evt.Input,
                },
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
        var metadata = new Dictionary<string, string>();
        foreach (var (key, value) in evt.Metadata)
            metadata[key] = value;
        events =
        [
            new StepFinishedEvent
            {
                Timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp),
                StepName = evt.StepId,
            },
            new CustomEvent
            {
                Timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp),
                Name = "aevatar.step.completed",
                Value = new
                {
                    evt.RunId,
                    evt.StepId,
                    evt.Success,
                    evt.Output,
                    evt.Error,
                    Metadata = metadata,
                },
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

        if (payload.Is(Aevatar.AI.Abstractions.TextMessageStartEvent.Descriptor))
        {
            var evt = payload.Unpack<Aevatar.AI.Abstractions.TextMessageStartEvent>();
            var msgId = AGUIEventEnvelopeMappingHelpers.ResolveMessageId(evt.SessionId, envelope.Id);
            events =
            [
                new Aevatar.Presentation.AGUI.TextMessageStartEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                    Role = "assistant",
                },
            ];
            return true;
        }

        if (payload.Is(Aevatar.AI.Abstractions.TextMessageContentEvent.Descriptor))
        {
            var evt = payload.Unpack<Aevatar.AI.Abstractions.TextMessageContentEvent>();
            var msgId = AGUIEventEnvelopeMappingHelpers.ResolveMessageId(evt.SessionId, envelope.Id);
            events =
            [
                new Aevatar.Presentation.AGUI.TextMessageContentEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                    Delta = evt.Delta,
                },
            ];
            return true;
        }

        if (payload.Is(Aevatar.AI.Abstractions.TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<Aevatar.AI.Abstractions.TextMessageEndEvent>();
            var msgId = AGUIEventEnvelopeMappingHelpers.ResolveMessageId(evt.SessionId, envelope.Id);
            if (string.IsNullOrWhiteSpace(evt.SessionId) && !string.IsNullOrWhiteSpace(evt.Content))
            {
                events =
                [
                    new Aevatar.Presentation.AGUI.TextMessageStartEvent
                    {
                        Timestamp = ts,
                        MessageId = msgId,
                        Role = "assistant",
                    },
                    new Aevatar.Presentation.AGUI.TextMessageContentEvent
                    {
                        Timestamp = ts,
                        MessageId = msgId,
                        Delta = evt.Content,
                    },
                    new Aevatar.Presentation.AGUI.TextMessageEndEvent
                    {
                        Timestamp = ts,
                        MessageId = msgId,
                    },
                ];
                return true;
            }

            events =
            [
                new Aevatar.Presentation.AGUI.TextMessageEndEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                },
            ];
            return true;
        }

        if (payload.Is(Aevatar.AI.Abstractions.ChatResponseEvent.Descriptor))
        {
            var evt = payload.Unpack<Aevatar.AI.Abstractions.ChatResponseEvent>();
            var msgId = AGUIEventEnvelopeMappingHelpers.ResolveMessageId(evt.SessionId, envelope.Id);
            events =
            [
                new Aevatar.Presentation.AGUI.TextMessageStartEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                    Role = "assistant",
                },
                new Aevatar.Presentation.AGUI.TextMessageContentEvent
                {
                    Timestamp = ts,
                    MessageId = msgId,
                    Delta = evt.Content,
                },
                new Aevatar.Presentation.AGUI.TextMessageEndEvent
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

public sealed class AIReasoningAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 35;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        if (envelope.Payload?.Is(Aevatar.AI.Abstractions.TextMessageReasoningEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<Aevatar.AI.Abstractions.TextMessageReasoningEvent>();
        events =
        [
            new CustomEvent
            {
                Timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp),
                Name = "aevatar.llm.reasoning",
                Value = new
                {
                    evt.SessionId,
                    evt.Delta,
                    Role = AGUIEventEnvelopeMappingHelpers.ResolveRoleFromPublisher(envelope.PublisherId),
                },
            },
        ];
        return true;
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
            var threadId = AGUIEventEnvelopeMappingHelpers.ResolveThreadId(envelope, evt.WorkflowName);
            var runId = AGUIEventEnvelopeMappingHelpers.ResolveRunId(envelope, threadId);
            events =
            [
                new RunFinishedEvent
                {
                    Timestamp = ts,
                    ThreadId = threadId,
                    RunId = runId,
                    Result = new { output = evt.Output },
                },
            ];
            return true;
        }

        var errorThreadId = AGUIEventEnvelopeMappingHelpers.ResolveThreadId(envelope, evt.WorkflowName);
        events =
        [
            new RunErrorEvent
            {
                Timestamp = ts,
                Message = evt.Error,
                RunId = AGUIEventEnvelopeMappingHelpers.ResolveRunId(envelope, errorThreadId),
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

        if (payload.Is(Aevatar.AI.Abstractions.ToolCallEvent.Descriptor))
        {
            var evt = payload.Unpack<Aevatar.AI.Abstractions.ToolCallEvent>();
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

        if (payload.Is(Aevatar.AI.Abstractions.ToolResultEvent.Descriptor))
        {
            var evt = payload.Unpack<Aevatar.AI.Abstractions.ToolResultEvent>();
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

public sealed class WorkflowWaitingSignalAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 46;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        if (envelope.Payload?.Is(WaitingForSignalEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<WaitingForSignalEvent>();
        var ts = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp);
        var runId = string.IsNullOrWhiteSpace(evt.RunId)
            ? AGUIEventEnvelopeMappingHelpers.ResolveRunId(envelope, string.Empty)
            : evt.RunId;

        events =
        [
            new CustomEvent
            {
                Timestamp = ts,
                Name = "aevatar.workflow.waiting_signal",
                Value = new
                {
                    RunId = runId,
                    evt.StepId,
                    evt.SignalName,
                    evt.Prompt,
                    evt.TimeoutMs,
                },
            },
        ];
        return true;
    }
}

public sealed class WorkflowSignalBufferedAGUIEventEnvelopeMappingHandler : IAGUIEventEnvelopeMappingHandler
{
    public int Order => 47;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<AGUIEvent> events)
    {
        if (envelope.Payload?.Is(WorkflowSignalBufferedEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<WorkflowSignalBufferedEvent>();
        events =
        [
            new CustomEvent
            {
                Timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp),
                Name = "aevatar.workflow.signal.buffered",
                Value = new
                {
                    evt.RunId,
                    evt.StepId,
                    evt.SignalName,
                    evt.Payload,
                    evt.ReceivedAtUnixTimeMs,
                },
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

    public static string ResolveRunId(EventEnvelope envelope, string fallbackThreadId)
    {
        return string.IsNullOrWhiteSpace(envelope.CorrelationId)
            ? fallbackThreadId
            : envelope.CorrelationId;
    }

    public static string ResolveMessageId(string? sessionId, string? envelopeId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
            return $"msg:{sessionId}";

        return $"msg:{envelopeId}";
    }

    public static string ResolveRoleFromPublisher(string? publisherId)
    {
        if (string.IsNullOrWhiteSpace(publisherId))
            return "assistant";

        var normalized = publisherId.Trim();
        var idx = normalized.LastIndexOf(':');
        if (idx >= 0 && idx < normalized.Length - 1)
            return normalized[(idx + 1)..];

        return normalized;
    }
}
