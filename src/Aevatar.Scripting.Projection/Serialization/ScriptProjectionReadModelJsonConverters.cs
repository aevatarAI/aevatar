using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.ReadModels;

internal sealed class ScriptReadModelDocumentJsonConverter : JsonConverter<ScriptReadModelDocument>
{
    public override ScriptReadModelDocument? Read(
        ref Utf8JsonReader reader,
        System.Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var result = new ScriptReadModelDocument
        {
            Id = ReadString(root, "id"),
            ScriptId = ReadString(root, "script_id"),
            DefinitionActorId = ReadString(root, "definition_actor_id"),
            Revision = ReadString(root, "revision"),
            ReadModelTypeUrl = ReadString(root, "read_model_type_url"),
            StateVersion = ReadInt64(root, "state_version"),
            LastEventId = ReadString(root, "last_event_id"),
            UpdatedAt = ReadDateTimeOffset(root, "updated_at"),
        };

        if (root.TryGetProperty("read_model_payload", out var payloadNode) &&
            payloadNode.ValueKind != JsonValueKind.Null)
        {
            result.ReadModelPayload = ReadAny(payloadNode);
        }

        return result;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ScriptReadModelDocument value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("script_id", value.ScriptId);
        writer.WriteString("definition_actor_id", value.DefinitionActorId);
        writer.WriteString("revision", value.Revision);
        writer.WriteString("read_model_type_url", value.ReadModelTypeUrl);
        writer.WritePropertyName("read_model_payload");
        WriteAny(writer, value.ReadModelPayload);
        writer.WriteNumber("state_version", value.StateVersion);
        writer.WriteString("last_event_id", value.LastEventId);
        writer.WriteString("updated_at", value.UpdatedAt.ToString("O"));
        writer.WriteEndObject();
    }

    private static Any? ReadAny(JsonElement payloadNode)
    {
        var typeUrl = ReadString(payloadNode, "type_url");
        var payloadBase64 = ReadString(payloadNode, "payload_base64");
        return new Any
        {
            TypeUrl = typeUrl,
            Value = string.IsNullOrWhiteSpace(payloadBase64)
                ? ByteString.Empty
                : ByteString.FromBase64(payloadBase64),
        };
    }

    private static void WriteAny(Utf8JsonWriter writer, Any? value)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("type_url", value.TypeUrl ?? string.Empty);
        writer.WriteString("payload_base64", value.Value?.ToBase64() ?? string.Empty);
        writer.WriteEndObject();
    }

    private static string ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var node)
            ? node.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static long ReadInt64(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var node) && node.TryGetInt64(out var value)
            ? value
            : 0L;

    private static DateTimeOffset ReadDateTimeOffset(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var node) && node.TryGetDateTimeOffset(out var value)
            ? value
            : default;
}

internal sealed class ScriptNativeDocumentReadModelJsonConverter : JsonConverter<ScriptNativeDocumentReadModel>
{
    public override ScriptNativeDocumentReadModel? Read(
        ref Utf8JsonReader reader,
        System.Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var result = new ScriptNativeDocumentReadModel
        {
            Id = ReadString(root, "id"),
            ScriptId = ReadString(root, "script_id"),
            DefinitionActorId = ReadString(root, "definition_actor_id"),
            Revision = ReadString(root, "revision"),
            SchemaId = ReadString(root, "schema_id"),
            SchemaVersion = ReadString(root, "schema_version"),
            SchemaHash = ReadString(root, "schema_hash"),
            DocumentIndexScope = ReadString(root, "document_index_scope"),
            StateVersion = ReadInt64(root, "state_version"),
            LastEventId = ReadString(root, "last_event_id"),
            UpdatedAt = ReadDateTimeOffset(root, "updated_at"),
        };

        if (root.TryGetProperty("fields", out var fieldsNode) &&
            fieldsNode.ValueKind == JsonValueKind.Object)
        {
            result.Fields = fieldsNode.EnumerateObject()
                .ToDictionary(
                    static property => property.Name,
                    static property => ScriptProjectionReadModelSupport.ReadJsonValue(property.Value),
                    StringComparer.Ordinal);
        }

        return result;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ScriptNativeDocumentReadModel value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("script_id", value.ScriptId);
        writer.WriteString("definition_actor_id", value.DefinitionActorId);
        writer.WriteString("revision", value.Revision);
        writer.WriteString("schema_id", value.SchemaId);
        writer.WriteString("schema_version", value.SchemaVersion);
        writer.WriteString("schema_hash", value.SchemaHash);
        writer.WriteString("document_index_scope", value.DocumentIndexScope);
        writer.WritePropertyName("fields");
        JsonSerializer.Serialize(writer, value.Fields, options);
        writer.WriteNumber("state_version", value.StateVersion);
        writer.WriteString("last_event_id", value.LastEventId);
        writer.WriteString("updated_at", value.UpdatedAt.ToString("O"));
        writer.WriteEndObject();
    }

    private static string ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var node)
            ? node.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static long ReadInt64(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var node) && node.TryGetInt64(out var value)
            ? value
            : 0L;

    private static DateTimeOffset ReadDateTimeOffset(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var node) && node.TryGetDateTimeOffset(out var value)
            ? value
            : default;
}
