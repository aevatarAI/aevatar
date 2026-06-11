using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Hosting.Serialization;

public sealed class StudioStepParametersJsonInputConverter : JsonConverter<StudioStepParameters>
{
    public override StudioStepParameters Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new StudioStepParameters();
        }

        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Studio step parameters must be a JSON object.");
        }

        return new StudioStepParameters(document.RootElement.EnumerateObject()
            .Select(property => new KeyValuePair<string, StudioStepParameterValue?>(
                property.Name,
                ToParameterValue(property.Value))));
    }

    public override void Write(
        Utf8JsonWriter writer,
        StudioStepParameters value,
        JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(
            writer,
            value.ToDictionary(
                parameter => parameter.Key,
                parameter => parameter.Value?.ToPlainValue(),
                StringComparer.Ordinal),
            options);
    }

    private static StudioStepParameterValue ToParameterValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null => StudioStepParameterValue.Null,
            JsonValueKind.String => StudioStepParameterValue.FromScalar(value.GetString()),
            JsonValueKind.True => StudioStepParameterValue.FromScalar(bool.TrueString.ToLowerInvariant()),
            JsonValueKind.False => StudioStepParameterValue.FromScalar(bool.FalseString.ToLowerInvariant()),
            JsonValueKind.Number => StudioStepParameterValue.FromScalar(value.GetRawText()),
            JsonValueKind.Array => StudioStepParameterValue.FromList(
                value.EnumerateArray().Select(ToParameterValue)),
            JsonValueKind.Object => StudioStepParameterValue.FromObject(
                value.EnumerateObject().Select(property =>
                    new KeyValuePair<string, StudioStepParameterValue?>(
                        property.Name,
                        ToParameterValue(property.Value)))),
            _ => StudioStepParameterValue.FromScalar(value.GetRawText()),
        };
}
