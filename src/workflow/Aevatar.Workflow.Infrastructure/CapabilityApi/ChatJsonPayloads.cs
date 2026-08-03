using System.Text.Json;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class ChatJsonPayloads
{
    private const string RawObservedEventName = "aevatar.raw.observed";

    private static readonly JsonFormatter Formatter = new(
        JsonFormatter.Settings.Default
            .WithFormatDefaultValues(false)
            .WithTypeRegistry(WorkflowJsonTypeRegistry.Default));

    public static string Format(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            return Formatter.Format(message);
        }
        // Raw observation can carry plugin-owned Any payloads that this host does not compile against.
        catch (InvalidOperationException) when (TryBuildOpaqueRawObservedFrame(message, out var fallback))
        {
            return Formatter.Format(fallback);
        }
    }

    public static JsonElement ToJsonElement(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var document = JsonDocument.Parse(Format(message));
        return document.RootElement.Clone();
    }

    private static bool TryBuildOpaqueRawObservedFrame(
        IMessage message,
        out WorkflowRunEventEnvelope fallback)
    {
        fallback = null!;
        if (message is not WorkflowRunEventEnvelope frame ||
            frame.Custom is not { } custom ||
            !string.Equals(custom.Name, RawObservedEventName, StringComparison.Ordinal) ||
            custom.Payload?.Is(WorkflowObservedEnvelopeCustomPayload.Descriptor) != true)
        {
            return false;
        }

        var observed = custom.Payload.Unpack<WorkflowObservedEnvelopeCustomPayload>();
        if (observed.Payload is null)
            return false;

        var opaqueObserved = observed.Clone();
        opaqueObserved.Payload = null;
        fallback = frame.Clone();
        fallback.Custom.Payload = Any.Pack(opaqueObserved);
        return true;
    }
}
