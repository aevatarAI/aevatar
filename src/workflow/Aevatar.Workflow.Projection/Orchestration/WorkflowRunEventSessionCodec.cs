using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;

namespace Aevatar.Workflow.Projection.Orchestration;

/// <summary>
/// Session event codec for workflow live run output events.
/// </summary>
public sealed class WorkflowRunEventSessionCodec : IProjectionSessionEventCodec<WorkflowRunEventEnvelope>
{
    public string Channel => "workflow-run";

    public string GetEventType(WorkflowRunEventEnvelope evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return WorkflowRunEventTypes.GetEventType(evt);
    }

    public ByteString Serialize(WorkflowRunEventEnvelope evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.ToByteString();
    }

    public WorkflowRunEventEnvelope? Deserialize(string eventType, ByteString payload)
    {
        if (string.IsNullOrWhiteSpace(eventType) || payload == null || payload.IsEmpty)
            return null;

        try
        {
            var decoded = WorkflowRunEventEnvelope.Parser.ParseFrom(payload);
            return string.Equals(WorkflowRunEventTypes.GetEventType(decoded), eventType, StringComparison.Ordinal)
                ? decoded
                : null;
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }
}
