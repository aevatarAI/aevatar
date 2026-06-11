using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Hosting.Serialization;

public sealed class EditorWorkflowDocumentJsonInputConverter : JsonConverter<WorkflowDocument>
{
    public override WorkflowDocument Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var document = JsonSerializer.Deserialize<WorkflowDocument>(
            ref reader,
            CreateDocumentSerializerOptions(options));

        return document ?? throw new JsonException("Workflow document is required.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        WorkflowDocument value,
        JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(
            writer,
            value,
            CreateDocumentSerializerOptions(options));
    }

    private static JsonSerializerOptions CreateDocumentSerializerOptions(JsonSerializerOptions options)
    {
        var documentOptions = new JsonSerializerOptions(options);
        for (var index = documentOptions.Converters.Count - 1; index >= 0; index--)
        {
            if (documentOptions.Converters[index] is EditorWorkflowDocumentJsonInputConverter)
            {
                documentOptions.Converters.RemoveAt(index);
            }
        }

        documentOptions.Converters.Insert(0, new StudioStepParametersJsonInputConverter());
        return documentOptions;
    }
}
