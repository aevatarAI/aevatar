using Aevatar.CQRS.Projection.Core.Abstractions;
using Google.Protobuf;

namespace Aevatar.Workflow.Projection.Orchestration;

internal static class WorkflowProjectionControlEventTypes
{
    public const string ReleaseRequested = "release_requested";

    public static string GetEventType(WorkflowProjectionControlEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.EventCase switch
        {
            WorkflowProjectionControlEvent.EventOneofCase.ReleaseRequested => ReleaseRequested,
            _ => string.Empty,
        };
    }
}

public sealed class WorkflowProjectionControlEventSessionCodec
    : IProjectionSessionEventCodec<WorkflowProjectionControlEvent>
{
    public string Channel => "workflow-projection-control";

    public string GetEventType(WorkflowProjectionControlEvent evt) =>
        WorkflowProjectionControlEventTypes.GetEventType(evt);

    public ByteString Serialize(WorkflowProjectionControlEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.ToByteString();
    }

    public WorkflowProjectionControlEvent? Deserialize(string eventType, ByteString payload)
    {
        if (string.IsNullOrWhiteSpace(eventType) || payload == null || payload.IsEmpty)
            return null;

        try
        {
            var decoded = WorkflowProjectionControlEvent.Parser.ParseFrom(payload);
            return string.Equals(
                WorkflowProjectionControlEventTypes.GetEventType(decoded),
                eventType,
                StringComparison.Ordinal)
                ? decoded
                : null;
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }
}
