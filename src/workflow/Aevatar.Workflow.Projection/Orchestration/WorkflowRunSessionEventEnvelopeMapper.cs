using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Transport;

namespace Aevatar.Workflow.Projection.Orchestration;

internal static class WorkflowRunSessionEventEnvelopeMapper
{
    public static WorkflowRunSessionEventEnvelope ToEnvelope(WorkflowRunEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var envelope = new WorkflowRunSessionEventEnvelope
        {
            Timestamp = evt.Timestamp,
        };

        switch (evt)
        {
            case WorkflowRunStartedEvent e:
                envelope.RunStarted = new WorkflowRunStartedEventPayload
                {
                    ThreadId = e.ThreadId,
                };
                break;
            case WorkflowRunFinishedEvent e:
                envelope.RunFinished = new WorkflowRunFinishedEventPayload
                {
                    ThreadId = e.ThreadId,
                    Result = WorkflowProjectionTransportValueCodec.Serialize(e.Result),
                };
                break;
            case WorkflowRunErrorEvent e:
                envelope.RunError = new WorkflowRunErrorEventPayload
                {
                    Message = e.Message,
                    Code = e.Code,
                };
                break;
            case WorkflowStepStartedEvent e:
                envelope.StepStarted = new WorkflowStepStartedEventPayload
                {
                    StepName = e.StepName,
                };
                break;
            case WorkflowStepFinishedEvent e:
                envelope.StepFinished = new WorkflowStepFinishedEventPayload
                {
                    StepName = e.StepName,
                };
                break;
            case WorkflowTextMessageStartEvent e:
                envelope.TextMessageStart = new WorkflowTextMessageStartEventPayload
                {
                    MessageId = e.MessageId,
                    Role = e.Role,
                };
                break;
            case WorkflowTextMessageContentEvent e:
                envelope.TextMessageContent = new WorkflowTextMessageContentEventPayload
                {
                    MessageId = e.MessageId,
                    Delta = e.Delta,
                };
                break;
            case WorkflowTextMessageEndEvent e:
                envelope.TextMessageEnd = new WorkflowTextMessageEndEventPayload
                {
                    MessageId = e.MessageId,
                };
                break;
            case WorkflowStateSnapshotEvent e:
                envelope.StateSnapshot = new WorkflowStateSnapshotEventPayload
                {
                    Snapshot = WorkflowProjectionTransportValueCodec.Serialize(e.Snapshot),
                };
                break;
            case WorkflowToolCallStartEvent e:
                envelope.ToolCallStart = new WorkflowToolCallStartEventPayload
                {
                    ToolCallId = e.ToolCallId,
                    ToolName = e.ToolName,
                };
                break;
            case WorkflowToolCallEndEvent e:
                envelope.ToolCallEnd = new WorkflowToolCallEndEventPayload
                {
                    ToolCallId = e.ToolCallId,
                    Result = e.Result,
                };
                break;
            case WorkflowCustomEvent e:
                envelope.Custom = new WorkflowCustomEventPayload
                {
                    Name = e.Name,
                    Value = WorkflowProjectionTransportValueCodec.Serialize(e.Value),
                };
                break;
            default:
                throw new InvalidOperationException($"Unsupported workflow run event '{evt.GetType().FullName}'.");
        }

        return envelope;
    }

    public static WorkflowRunEvent? FromEnvelope(WorkflowRunSessionEventEnvelope envelope) =>
        envelope.EventCase switch
        {
            WorkflowRunSessionEventEnvelope.EventOneofCase.RunStarted => new WorkflowRunStartedEvent
            {
                Timestamp = envelope.Timestamp,
                ThreadId = envelope.RunStarted.ThreadId,
            },
            WorkflowRunSessionEventEnvelope.EventOneofCase.RunFinished => new WorkflowRunFinishedEvent
            {
                Timestamp = envelope.Timestamp,
                ThreadId = envelope.RunFinished.ThreadId,
                Result = WorkflowProjectionTransportValueCodec.Deserialize(envelope.RunFinished.Result),
            },
            WorkflowRunSessionEventEnvelope.EventOneofCase.RunError => new WorkflowRunErrorEvent
            {
                Timestamp = envelope.Timestamp,
                Message = envelope.RunError.Message,
                Code = envelope.RunError.Code,
            },
            WorkflowRunSessionEventEnvelope.EventOneofCase.StepStarted => new WorkflowStepStartedEvent
            {
                Timestamp = envelope.Timestamp,
                StepName = envelope.StepStarted.StepName,
            },
            WorkflowRunSessionEventEnvelope.EventOneofCase.StepFinished => new WorkflowStepFinishedEvent
            {
                Timestamp = envelope.Timestamp,
                StepName = envelope.StepFinished.StepName,
            },
            WorkflowRunSessionEventEnvelope.EventOneofCase.TextMessageStart => new WorkflowTextMessageStartEvent
            {
                Timestamp = envelope.Timestamp,
                MessageId = envelope.TextMessageStart.MessageId,
                Role = envelope.TextMessageStart.Role,
            },
            WorkflowRunSessionEventEnvelope.EventOneofCase.TextMessageContent => new WorkflowTextMessageContentEvent
            {
                Timestamp = envelope.Timestamp,
                MessageId = envelope.TextMessageContent.MessageId,
                Delta = envelope.TextMessageContent.Delta,
            },
            WorkflowRunSessionEventEnvelope.EventOneofCase.TextMessageEnd => new WorkflowTextMessageEndEvent
            {
                Timestamp = envelope.Timestamp,
                MessageId = envelope.TextMessageEnd.MessageId,
            },
            WorkflowRunSessionEventEnvelope.EventOneofCase.StateSnapshot => new WorkflowStateSnapshotEvent
            {
                Timestamp = envelope.Timestamp,
                Snapshot = WorkflowProjectionTransportValueCodec.Deserialize(envelope.StateSnapshot.Snapshot) ??
                    new Dictionary<string, object?>(StringComparer.Ordinal),
            },
            WorkflowRunSessionEventEnvelope.EventOneofCase.ToolCallStart => new WorkflowToolCallStartEvent
            {
                Timestamp = envelope.Timestamp,
                ToolCallId = envelope.ToolCallStart.ToolCallId,
                ToolName = envelope.ToolCallStart.ToolName,
            },
            WorkflowRunSessionEventEnvelope.EventOneofCase.ToolCallEnd => new WorkflowToolCallEndEvent
            {
                Timestamp = envelope.Timestamp,
                ToolCallId = envelope.ToolCallEnd.ToolCallId,
                Result = envelope.ToolCallEnd.Result,
            },
            WorkflowRunSessionEventEnvelope.EventOneofCase.Custom => new WorkflowCustomEvent
            {
                Timestamp = envelope.Timestamp,
                Name = envelope.Custom.Name,
                Value = WorkflowProjectionTransportValueCodec.Deserialize(envelope.Custom.Value),
            },
            _ => null,
        };
}
