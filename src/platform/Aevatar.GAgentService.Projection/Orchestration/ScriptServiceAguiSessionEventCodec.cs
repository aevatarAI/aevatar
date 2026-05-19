using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Presentation.AGUI;
using Google.Protobuf;

namespace Aevatar.GAgentService.Projection.Orchestration;

// Fix (pr678-review): script-service AGUI projection had no codec of its own and
//   silently rode on GAgentDraftRunSessionEventCodec through a shared
//   IProjectionSessionEventCodec<AGUIEvent> registration. It now owns a distinct
//   channel so its session streams are isolated from the draft-run pipeline.
public sealed class ScriptServiceAguiSessionEventCodec : IProjectionSessionEventCodec<AGUIEvent>
{
    public string Channel => "script-service-agui-session";

    public string GetEventType(AGUIEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.EventCase == AGUIEvent.EventOneofCase.None
            ? AGUIEvent.Descriptor.FullName
            : evt.EventCase.ToString();
    }

    public ByteString Serialize(AGUIEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.ToByteString();
    }

    public AGUIEvent? Deserialize(string eventType, ByteString payload)
    {
        if (string.IsNullOrWhiteSpace(eventType) || payload == null || payload.IsEmpty)
            return null;

        try
        {
            var decoded = AGUIEvent.Parser.ParseFrom(payload);
            return string.Equals(GetEventType(decoded), eventType, StringComparison.Ordinal)
                ? decoded
                : null;
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }
}
