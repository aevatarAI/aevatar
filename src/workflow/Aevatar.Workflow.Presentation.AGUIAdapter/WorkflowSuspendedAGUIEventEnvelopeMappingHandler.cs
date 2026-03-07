using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

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
        if (!string.IsNullOrWhiteSpace(evt.ResumeToken))
            metadata["resume_token"] = evt.ResumeToken;

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

