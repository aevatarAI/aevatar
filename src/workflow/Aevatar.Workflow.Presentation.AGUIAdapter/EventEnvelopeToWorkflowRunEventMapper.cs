using Aevatar.Foundation.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public interface IEventEnvelopeToWorkflowRunEventMapper
{
    IReadOnlyList<WorkflowRunEventEnvelope> Map(EventEnvelope envelope);
}

public interface IWorkflowRunEventEnvelopeMappingHandler
{
    int Order { get; }

    bool TryMap(EventEnvelope envelope, out IReadOnlyList<WorkflowRunEventEnvelope> events);
}

public sealed class EventEnvelopeToWorkflowRunEventMapper : IEventEnvelopeToWorkflowRunEventMapper
{
    private readonly IReadOnlyList<IWorkflowRunEventEnvelopeMappingHandler> _handlers;

    public EventEnvelopeToWorkflowRunEventMapper(IEnumerable<IWorkflowRunEventEnvelopeMappingHandler> handlers)
    {
        _handlers = handlers.OrderBy(x => x.Order).ToList();
    }

    public IReadOnlyList<WorkflowRunEventEnvelope> Map(EventEnvelope envelope)
    {
        if (!CommittedStateEventEnvelope.TryCreateObservedEnvelope(envelope, out var observed) ||
            observed?.Payload == null)
        {
            return [];
        }

        var output = new List<WorkflowRunEventEnvelope>();
        foreach (var handler in _handlers)
        {
            if (!handler.TryMap(observed, out var mapped) || mapped.Count == 0)
                continue;

            output.AddRange(mapped);
        }

        return output;
    }
}

public sealed class StartWorkflowRunEventEnvelopeMappingHandler : IWorkflowRunEventEnvelopeMappingHandler
{
    public int Order => 0;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<WorkflowRunEventEnvelope> events)
    {
        if (envelope.Payload?.Is(StartWorkflowEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<StartWorkflowEvent>();
        var threadId = AGUIEventEnvelopeMappingHelpers.ResolveThreadId(envelope, evt.WorkflowName);
        events =
        [
            new WorkflowRunEventEnvelope
            {
                Timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp),
                RunStarted = new WorkflowRunStartedEventPayload
                {
                    ThreadId = threadId,
                },
            },
        ];
        return true;
    }
}

public sealed class StepRequestRunEventEnvelopeMappingHandler : IWorkflowRunEventEnvelopeMappingHandler
{
    public int Order => 10;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<WorkflowRunEventEnvelope> events)
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
            new WorkflowRunEventEnvelope
            {
                Timestamp = ts,
                StepStarted = new WorkflowStepStartedEventPayload
                {
                    StepName = evt.StepId,
                },
            },
            new WorkflowRunEventEnvelope
            {
                Timestamp = ts,
                Custom = new WorkflowCustomEventPayload
                {
                    Name = "aevatar.step.request",
                    Payload = Any.Pack(new WorkflowStepRequestCustomPayload
                    {
                        RunId = evt.RunId,
                        StepId = evt.StepId,
                        StepType = evt.StepType,
                        TargetRole = evt.TargetRole,
                        Input = evt.Input,
                    }),
                },
            },
        ];
        return true;
    }
}

public sealed class StepCompletedRunEventEnvelopeMappingHandler : IWorkflowRunEventEnvelopeMappingHandler
{
    public int Order => 20;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<WorkflowRunEventEnvelope> events)
    {
        if (envelope.Payload?.Is(StepCompletedEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<StepCompletedEvent>();
        var annotations = new Dictionary<string, string>();
        foreach (var (key, value) in evt.Annotations)
            annotations[key] = value;
        events =
        [
            new WorkflowRunEventEnvelope
            {
                Timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp),
                StepFinished = new WorkflowStepFinishedEventPayload
                {
                    StepName = evt.StepId,
                },
            },
            new WorkflowRunEventEnvelope
            {
                Timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp),
                Custom = new WorkflowCustomEventPayload
                {
                    Name = "aevatar.step.completed",
                    Payload = Any.Pack(new WorkflowStepCompletedCustomPayload
                    {
                        RunId = evt.RunId,
                        StepId = evt.StepId,
                        Success = evt.Success,
                        Output = evt.Output,
                        Error = evt.Error,
                        Annotations = { annotations },
                        NextStepId = evt.NextStepId,
                        BranchKey = evt.BranchKey,
                        AssignedVariable = evt.AssignedVariable,
                        AssignedValue = evt.AssignedValue,
                    }),
                },
            },
        ];
        return true;
    }
}

public sealed class AITextStreamRunEventEnvelopeMappingHandler : IWorkflowRunEventEnvelopeMappingHandler
{
    public int Order => 30;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<WorkflowRunEventEnvelope> events)
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
                new WorkflowRunEventEnvelope
                {
                    Timestamp = ts,
                    TextMessageStart = new WorkflowTextMessageStartEventPayload
                    {
                        MessageId = msgId,
                        Role = "assistant",
                    },
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
                new WorkflowRunEventEnvelope
                {
                    Timestamp = ts,
                    TextMessageContent = new WorkflowTextMessageContentEventPayload
                    {
                        MessageId = msgId,
                        Delta = evt.Delta,
                    },
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
                    new WorkflowRunEventEnvelope
                    {
                        Timestamp = ts,
                        TextMessageStart = new WorkflowTextMessageStartEventPayload
                        {
                            MessageId = msgId,
                            Role = "assistant",
                        },
                    },
                    new WorkflowRunEventEnvelope
                    {
                        Timestamp = ts,
                        TextMessageContent = new WorkflowTextMessageContentEventPayload
                        {
                            MessageId = msgId,
                            Delta = evt.Content,
                        },
                    },
                    new WorkflowRunEventEnvelope
                    {
                        Timestamp = ts,
                        TextMessageEnd = new WorkflowTextMessageEndEventPayload
                        {
                            MessageId = msgId,
                        },
                    },
                ];
                return true;
            }

            events =
            [
                new WorkflowRunEventEnvelope
                {
                    Timestamp = ts,
                    TextMessageEnd = new WorkflowTextMessageEndEventPayload
                    {
                        MessageId = msgId,
                    },
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
                new WorkflowRunEventEnvelope
                {
                    Timestamp = ts,
                    TextMessageStart = new WorkflowTextMessageStartEventPayload
                    {
                        MessageId = msgId,
                        Role = "assistant",
                    },
                },
                new WorkflowRunEventEnvelope
                {
                    Timestamp = ts,
                    TextMessageContent = new WorkflowTextMessageContentEventPayload
                    {
                        MessageId = msgId,
                        Delta = evt.Content,
                    },
                },
                new WorkflowRunEventEnvelope
                {
                    Timestamp = ts,
                    TextMessageEnd = new WorkflowTextMessageEndEventPayload
                    {
                        MessageId = msgId,
                    },
                },
            ];
            return true;
        }

        return false;
    }
}

public sealed class AIReasoningRunEventEnvelopeMappingHandler : IWorkflowRunEventEnvelopeMappingHandler
{
    public int Order => 35;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<WorkflowRunEventEnvelope> events)
    {
        if (envelope.Payload?.Is(Aevatar.AI.Abstractions.TextMessageReasoningEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<Aevatar.AI.Abstractions.TextMessageReasoningEvent>();
        events =
        [
            new WorkflowRunEventEnvelope
            {
                Timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp),
                Custom = new WorkflowCustomEventPayload
                {
                    Name = "aevatar.llm.reasoning",
                    Payload = Any.Pack(new WorkflowReasoningCustomPayload
                    {
                        SessionId = evt.SessionId,
                        Delta = evt.Delta,
                        Role = AGUIEventEnvelopeMappingHelpers.ResolveRoleFromEnvelope(envelope),
                    }),
                },
            },
        ];
        return true;
    }
}

public sealed class WorkflowCompletedRunEventEnvelopeMappingHandler : IWorkflowRunEventEnvelopeMappingHandler
{
    public int Order => 40;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<WorkflowRunEventEnvelope> events)
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
            events =
            [
                new WorkflowRunEventEnvelope
                {
                    Timestamp = ts,
                    RunFinished = new WorkflowRunFinishedEventPayload
                    {
                        ThreadId = threadId,
                        Result = Any.Pack(new WorkflowRunResultPayload
                        {
                            Output = evt.Output,
                        }),
                    },
                },
            ];
            return true;
        }

        events =
        [
            new WorkflowRunEventEnvelope
            {
                Timestamp = ts,
                RunError = new WorkflowRunErrorEventPayload
                {
                    Message = evt.Error,
                    Code = "WORKFLOW_FAILED",
                },
            },
        ];
        return true;
    }
}

public sealed class ToolCallRunEventEnvelopeMappingHandler : IWorkflowRunEventEnvelopeMappingHandler
{
    public int Order => 50;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<WorkflowRunEventEnvelope> events)
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
                new WorkflowRunEventEnvelope
                {
                    Timestamp = ts,
                    ToolCallStart = new WorkflowToolCallStartEventPayload
                    {
                        ToolCallId = evt.CallId,
                        ToolName = evt.ToolName,
                    },
                },
            ];
            return true;
        }

        if (payload.Is(Aevatar.AI.Abstractions.ToolResultEvent.Descriptor))
        {
            var evt = payload.Unpack<Aevatar.AI.Abstractions.ToolResultEvent>();
            events =
            [
                new WorkflowRunEventEnvelope
                {
                    Timestamp = ts,
                    ToolCallEnd = new WorkflowToolCallEndEventPayload
                    {
                        ToolCallId = evt.CallId,
                        Result = evt.ResultJson,
                    },
                },
            ];
            return true;
        }

        return false;
    }
}

public sealed class WorkflowSuspendedRunEventEnvelopeMappingHandler : IWorkflowRunEventEnvelopeMappingHandler
{
    public int Order => 45;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<WorkflowRunEventEnvelope> events)
    {
        if (envelope.Payload?.Is(WorkflowSuspendedEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<WorkflowSuspendedEvent>();
        var ts = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in evt.Metadata)
            metadata[key] = value;

        events =
        [
            new WorkflowRunEventEnvelope
            {
                Timestamp = ts,
                Custom = new WorkflowCustomEventPayload
                {
                    Name = "aevatar.human_input.request",
                    Payload = Any.Pack(new WorkflowHumanInputRequestCustomPayload
                    {
                        StepId = evt.StepId,
                        RunId = evt.RunId,
                        SuspensionType = evt.SuspensionType,
                        Prompt = evt.Prompt,
                        TimeoutSeconds = evt.TimeoutSeconds,
                        VariableName = evt.VariableName,
                        Metadata = { metadata },
                    }),
                },
            },
        ];
        return true;
    }
}

public sealed class WorkflowWaitingSignalRunEventEnvelopeMappingHandler : IWorkflowRunEventEnvelopeMappingHandler
{
    public int Order => 46;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<WorkflowRunEventEnvelope> events)
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
            new WorkflowRunEventEnvelope
            {
                Timestamp = ts,
                Custom = new WorkflowCustomEventPayload
                {
                    Name = "aevatar.workflow.waiting_signal",
                    Payload = Any.Pack(new WorkflowWaitingSignalCustomPayload
                    {
                        RunId = runId,
                        StepId = evt.StepId,
                        SignalName = evt.SignalName,
                        Prompt = evt.Prompt,
                        TimeoutMs = evt.TimeoutMs,
                    }),
                },
            },
        ];
        return true;
    }
}

public sealed class WorkflowSignalBufferedRunEventEnvelopeMappingHandler : IWorkflowRunEventEnvelopeMappingHandler
{
    public int Order => 47;

    public bool TryMap(EventEnvelope envelope, out IReadOnlyList<WorkflowRunEventEnvelope> events)
    {
        if (envelope.Payload?.Is(WorkflowSignalBufferedEvent.Descriptor) != true)
        {
            events = [];
            return false;
        }

        var evt = envelope.Payload.Unpack<WorkflowSignalBufferedEvent>();
        events =
        [
            new WorkflowRunEventEnvelope
            {
                Timestamp = AGUIEventEnvelopeMappingHelpers.ToUnixMs(envelope.Timestamp),
                Custom = new WorkflowCustomEventPayload
                {
                    Name = "aevatar.workflow.signal.buffered",
                    Payload = Any.Pack(new WorkflowSignalBufferedCustomPayload
                    {
                        RunId = evt.RunId,
                        StepId = evt.StepId,
                        SignalName = evt.SignalName,
                        Payload = evt.Payload,
                        ReceivedAtUnixTimeMs = evt.ReceivedAtUnixTimeMs,
                    }),
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
        var publisherActorId = envelope.Route?.PublisherActorId;
        return string.IsNullOrWhiteSpace(publisherActorId)
            ? fallback
            : publisherActorId;
    }

    public static string ResolveRunId(EventEnvelope envelope, string fallbackThreadId)
    {
        var correlationId = envelope.Propagation?.CorrelationId;
        return string.IsNullOrWhiteSpace(correlationId)
            ? fallbackThreadId
            : correlationId;
    }

    public static string ResolveMessageId(string? sessionId, string? envelopeId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
            return $"msg:{sessionId}";

        return $"msg:{envelopeId}";
    }

    public static string ResolveRoleFromEnvelope(EventEnvelope envelope)
    {
        return ResolveRoleFromPublisher(envelope.Route?.PublisherActorId);
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
