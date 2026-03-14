using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.Scripting.Projection.Serialization;

internal sealed class ProtobufAnyBase64JsonConverter : JsonConverter<Any>
{
    private const string TypeUrlProperty = "type_url";
    private const string PayloadBase64Property = "payload_base64";

    public override Any? Read(
        ref Utf8JsonReader reader,
        System.Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var typeUrl = root.TryGetProperty(TypeUrlProperty, out var typeUrlNode)
            ? typeUrlNode.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        var payloadBase64 = root.TryGetProperty(PayloadBase64Property, out var payloadNode)
            ? payloadNode.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        var payload = payloadBase64.Length == 0
            ? ByteString.Empty
            : ByteString.FromBase64(payloadBase64);
        return new Any
        {
            TypeUrl = typeUrl,
            Value = payload,
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        Any value,
        JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString(TypeUrlProperty, value.TypeUrl ?? string.Empty);
        writer.WriteString(PayloadBase64Property, value.Value?.ToBase64() ?? string.Empty);
        writer.WriteEndObject();
    }
}
