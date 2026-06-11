using System.Text.Json;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class ChatJsonPayloads
{
    private static readonly JsonFormatter Formatter = new(
        JsonFormatter.Settings.Default
            .WithFormatDefaultValues(false)
            .WithTypeRegistry(WorkflowJsonTypeRegistry.Default));

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
