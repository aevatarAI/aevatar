using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Scripting.Abstractions;
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

internal sealed class ScriptDefinitionSnapshotDocumentJsonConverter : JsonConverter<ScriptDefinitionSnapshotDocument>
{
    public override ScriptDefinitionSnapshotDocument? Read(
        ref Utf8JsonReader reader,
        System.Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        return new ScriptDefinitionSnapshotDocument
        {
            Id = ScriptProjectionJsonConverterSupport.ReadString(root, "id"),
            StateVersion = ScriptProjectionJsonConverterSupport.ReadInt64(root, "state_version"),
            LastEventId = ScriptProjectionJsonConverterSupport.ReadString(root, "last_event_id"),
            CreatedAt = ScriptProjectionJsonConverterSupport.ReadDateTimeOffset(root, "created_at"),
            UpdatedAt = ScriptProjectionJsonConverterSupport.ReadDateTimeOffset(root, "updated_at"),
            ScriptId = ScriptProjectionJsonConverterSupport.ReadString(root, "script_id"),
            DefinitionActorId = ScriptProjectionJsonConverterSupport.ReadString(root, "definition_actor_id"),
            Revision = ScriptProjectionJsonConverterSupport.ReadString(root, "revision"),
            SourceText = ScriptProjectionJsonConverterSupport.ReadString(root, "source_text"),
            SourceHash = ScriptProjectionJsonConverterSupport.ReadString(root, "source_hash"),
            StateTypeUrl = ScriptProjectionJsonConverterSupport.ReadString(root, "state_type_url"),
            ReadModelTypeUrl = ScriptProjectionJsonConverterSupport.ReadString(root, "read_model_type_url"),
            ReadModelSchemaVersion = ScriptProjectionJsonConverterSupport.ReadString(root, "read_model_schema_version"),
            ReadModelSchemaHash = ScriptProjectionJsonConverterSupport.ReadString(root, "read_model_schema_hash"),
            ScriptPackage = ScriptProjectionJsonConverterSupport.ReadMessage<ScriptPackageSpec>(root, "script_package")
                ?? new ScriptPackageSpec(),
            ProtocolDescriptorSetBase64 =
                ScriptProjectionJsonConverterSupport.ReadString(root, "protocol_descriptor_set_base64"),
            StateDescriptorFullName =
                ScriptProjectionJsonConverterSupport.ReadString(root, "state_descriptor_full_name"),
            ReadModelDescriptorFullName =
                ScriptProjectionJsonConverterSupport.ReadString(root, "read_model_descriptor_full_name"),
            RuntimeSemantics =
                ScriptProjectionJsonConverterSupport.ReadMessage<ScriptRuntimeSemanticsSpec>(root, "runtime_semantics")
                ?? new ScriptRuntimeSemanticsSpec(),
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        ScriptDefinitionSnapshotDocument value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteNumber("state_version", value.StateVersion);
        writer.WriteString("last_event_id", value.LastEventId);
        writer.WriteString("created_at", value.CreatedAt.ToString("O"));
        writer.WriteString("updated_at", value.UpdatedAt.ToString("O"));
        writer.WriteString("script_id", value.ScriptId);
        writer.WriteString("definition_actor_id", value.DefinitionActorId);
        writer.WriteString("revision", value.Revision);
        writer.WriteString("source_text", value.SourceText);
        writer.WriteString("source_hash", value.SourceHash);
        writer.WriteString("state_type_url", value.StateTypeUrl);
        writer.WriteString("read_model_type_url", value.ReadModelTypeUrl);
        writer.WriteString("read_model_schema_version", value.ReadModelSchemaVersion);
        writer.WriteString("read_model_schema_hash", value.ReadModelSchemaHash);
        writer.WritePropertyName("script_package");
        ScriptProjectionJsonConverterSupport.WriteMessage(writer, value.ScriptPackage);
        writer.WriteString("protocol_descriptor_set_base64", value.ProtocolDescriptorSetBase64);
        writer.WriteString("state_descriptor_full_name", value.StateDescriptorFullName);
        writer.WriteString("read_model_descriptor_full_name", value.ReadModelDescriptorFullName);
        writer.WritePropertyName("runtime_semantics");
        ScriptProjectionJsonConverterSupport.WriteMessage(writer, value.RuntimeSemantics);
        writer.WriteEndObject();
    }
}

internal sealed class ScriptCatalogEntryDocumentJsonConverter : JsonConverter<ScriptCatalogEntryDocument>
{
    public override ScriptCatalogEntryDocument? Read(
        ref Utf8JsonReader reader,
        System.Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var result = new ScriptCatalogEntryDocument
        {
            Id = ScriptProjectionJsonConverterSupport.ReadString(root, "id"),
            StateVersion = ScriptProjectionJsonConverterSupport.ReadInt64(root, "state_version"),
            LastEventId = ScriptProjectionJsonConverterSupport.ReadString(root, "last_event_id"),
            CreatedAt = ScriptProjectionJsonConverterSupport.ReadDateTimeOffset(root, "created_at"),
            UpdatedAt = ScriptProjectionJsonConverterSupport.ReadDateTimeOffset(root, "updated_at"),
            CatalogActorId = ScriptProjectionJsonConverterSupport.ReadString(root, "catalog_actor_id"),
            ScriptId = ScriptProjectionJsonConverterSupport.ReadString(root, "script_id"),
            ActiveRevision = ScriptProjectionJsonConverterSupport.ReadString(root, "active_revision"),
            ActiveDefinitionActorId = ScriptProjectionJsonConverterSupport.ReadString(root, "active_definition_actor_id"),
            ActiveSourceHash = ScriptProjectionJsonConverterSupport.ReadString(root, "active_source_hash"),
            PreviousRevision = ScriptProjectionJsonConverterSupport.ReadString(root, "previous_revision"),
            LastProposalId = ScriptProjectionJsonConverterSupport.ReadString(root, "last_proposal_id"),
        };
        result.RevisionHistoryEntries.Add(
            ScriptProjectionJsonConverterSupport.ReadStringArray(root, "revision_history_entries"));
        return result;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ScriptCatalogEntryDocument value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteNumber("state_version", value.StateVersion);
        writer.WriteString("last_event_id", value.LastEventId);
        writer.WriteString("created_at", value.CreatedAt.ToString("O"));
        writer.WriteString("updated_at", value.UpdatedAt.ToString("O"));
        writer.WriteString("catalog_actor_id", value.CatalogActorId);
        writer.WriteString("script_id", value.ScriptId);
        writer.WriteString("active_revision", value.ActiveRevision);
        writer.WriteString("active_definition_actor_id", value.ActiveDefinitionActorId);
        writer.WriteString("active_source_hash", value.ActiveSourceHash);
        writer.WriteString("previous_revision", value.PreviousRevision);
        writer.WritePropertyName("revision_history_entries");
        ScriptProjectionJsonConverterSupport.WriteStringArray(writer, value.RevisionHistoryEntries);
        writer.WriteString("last_proposal_id", value.LastProposalId);
        writer.WriteEndObject();
    }
}

internal sealed class ScriptEvolutionReadModelJsonConverter : JsonConverter<ScriptEvolutionReadModel>
{
    public override ScriptEvolutionReadModel? Read(
        ref Utf8JsonReader reader,
        System.Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var result = new ScriptEvolutionReadModel
        {
            Id = ScriptProjectionJsonConverterSupport.ReadString(root, "id"),
            ProposalId = ScriptProjectionJsonConverterSupport.ReadString(root, "proposal_id"),
            ScriptId = ScriptProjectionJsonConverterSupport.ReadString(root, "script_id"),
            BaseRevision = ScriptProjectionJsonConverterSupport.ReadString(root, "base_revision"),
            CandidateRevision = ScriptProjectionJsonConverterSupport.ReadString(root, "candidate_revision"),
            ValidationStatus = ScriptProjectionJsonConverterSupport.ReadString(root, "validation_status"),
            PromotionStatus = ScriptProjectionJsonConverterSupport.ReadString(root, "promotion_status"),
            RollbackStatus = ScriptProjectionJsonConverterSupport.ReadString(root, "rollback_status"),
            FailureReason = ScriptProjectionJsonConverterSupport.ReadString(root, "failure_reason"),
            DefinitionActorId = ScriptProjectionJsonConverterSupport.ReadString(root, "definition_actor_id"),
            CatalogActorId = ScriptProjectionJsonConverterSupport.ReadString(root, "catalog_actor_id"),
            LastEventId = ScriptProjectionJsonConverterSupport.ReadString(root, "last_event_id"),
            UpdatedAt = ScriptProjectionJsonConverterSupport.ReadDateTimeOffset(root, "updated_at"),
            StateVersion = ScriptProjectionJsonConverterSupport.ReadInt64(root, "state_version"),
            ActorId = ScriptProjectionJsonConverterSupport.ReadString(root, "actor_id"),
        };
        result.DiagnosticsEntries.Add(
            ScriptProjectionJsonConverterSupport.ReadStringArray(root, "diagnostics_entries"));
        return result;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ScriptEvolutionReadModel value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("proposal_id", value.ProposalId);
        writer.WriteString("script_id", value.ScriptId);
        writer.WriteString("base_revision", value.BaseRevision);
        writer.WriteString("candidate_revision", value.CandidateRevision);
        writer.WriteString("validation_status", value.ValidationStatus);
        writer.WriteString("promotion_status", value.PromotionStatus);
        writer.WriteString("rollback_status", value.RollbackStatus);
        writer.WriteString("failure_reason", value.FailureReason);
        writer.WriteString("definition_actor_id", value.DefinitionActorId);
        writer.WriteString("catalog_actor_id", value.CatalogActorId);
        writer.WritePropertyName("diagnostics_entries");
        ScriptProjectionJsonConverterSupport.WriteStringArray(writer, value.DiagnosticsEntries);
        writer.WriteString("last_event_id", value.LastEventId);
        writer.WriteString("updated_at", value.UpdatedAt.ToString("O"));
        writer.WriteNumber("state_version", value.StateVersion);
        writer.WriteString("actor_id", value.ActorId);
        writer.WriteEndObject();
    }
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

internal static class ScriptProjectionJsonConverterSupport
{
    public static string ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var node)
            ? node.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    public static long ReadInt64(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var node) && node.TryGetInt64(out var value)
            ? value
            : 0L;

    public static DateTimeOffset ReadDateTimeOffset(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var node) && node.TryGetDateTimeOffset(out var value)
            ? value
            : default;

    public static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<string>();
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                values.Add(item.GetString()?.Trim() ?? string.Empty);
        }

        return values;
    }

    public static void WriteStringArray(Utf8JsonWriter writer, IEnumerable<string> values)
    {
        writer.WriteStartArray();
        foreach (var value in values)
            writer.WriteStringValue(value ?? string.Empty);
        writer.WriteEndArray();
    }

    public static T? ReadMessage<T>(JsonElement root, string propertyName)
        where T : class, IMessage<T>, new()
    {
        if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind == JsonValueKind.Null)
            return null;

        return JsonParser.Default.Parse<T>(node.GetRawText());
    }

    public static void WriteMessage<T>(Utf8JsonWriter writer, T? value)
        where T : class, IMessage<T>
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteRawValue(JsonFormatter.Default.Format(value), skipInputValidation: true);
    }
}
