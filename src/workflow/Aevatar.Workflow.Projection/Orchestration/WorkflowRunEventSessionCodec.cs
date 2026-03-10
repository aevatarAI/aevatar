using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Transport;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Orchestration;

/// <summary>
/// Session event codec for workflow live run output events.
/// </summary>
public sealed class WorkflowRunEventSessionCodec : IProjectionSessionEventCodec<WorkflowRunEvent>
{
    public string Channel => "workflow-run";

    public string GetEventType(WorkflowRunEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.Type;
    }

    public Any Serialize(WorkflowRunEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return Any.Pack(WorkflowRunSessionEventEnvelopeMapper.ToEnvelope(evt));
    }

    public WorkflowRunEvent? Deserialize(string eventType, Any payload)
    {
        if (string.IsNullOrWhiteSpace(eventType) ||
            payload == null ||
            !payload.Is(WorkflowRunSessionEventEnvelope.Descriptor))
        {
            return null;
        }

        try
        {
            var evt = WorkflowRunSessionEventEnvelopeMapper.FromEnvelope(payload.Unpack<WorkflowRunSessionEventEnvelope>());
            return string.Equals(evt?.Type, eventType, StringComparison.Ordinal)
                ? evt
                : null;
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }
}
