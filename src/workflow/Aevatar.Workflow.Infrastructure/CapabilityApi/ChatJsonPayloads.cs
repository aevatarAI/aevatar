using System.Text.Json;
using Aevatar.AI.Abstractions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class ChatJsonPayloads
{
    private static readonly TypeRegistry WorkflowTypeRegistry = TypeRegistry.FromFiles(
        AiMessagesReflection.Descriptor,
        WorkflowRunEventEnvelope.Descriptor.File,
        AnyReflection.Descriptor,
        StructReflection.Descriptor,
        TimestampReflection.Descriptor,
        WrappersReflection.Descriptor);
    private static readonly JsonFormatter Formatter = new(
        JsonFormatter.Settings.Default
            .WithFormatDefaultValues(false)
            .WithTypeRegistry(WorkflowTypeRegistry));

    public static string Format(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Formatter.Format(message);
    }

    public static JsonElement ToJsonElement(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var document = JsonDocument.Parse(Format(message));
        return document.RootElement.Clone();
    }
}
