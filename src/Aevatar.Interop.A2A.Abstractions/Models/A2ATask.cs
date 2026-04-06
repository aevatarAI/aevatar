using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.Interop.A2A.Abstractions.Models;

/// <summary>A2A Task — 表示一次跨 agent 交互的任务。</summary>
public sealed class A2ATask
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("status")]
    public required A2ATaskStatus Status { get; set; }

    [JsonPropertyName("history")]
    public List<Message>? History { get; set; }

    [JsonPropertyName("artifacts")]
    public List<Artifact>? Artifacts { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>A2A Task 状态。</summary>
public sealed class A2ATaskStatus
{
    [JsonPropertyName("state")]
    public required TaskState State { get; set; }

    [JsonPropertyName("message")]
    public Message? Message { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<TaskState>))]
public enum TaskState
{
    [JsonStringEnumMemberName("submitted")]
    Submitted,

    [JsonStringEnumMemberName("working")]
    Working,

    [JsonStringEnumMemberName("input-required")]
    InputRequired,

    [JsonStringEnumMemberName("completed")]
    Completed,

    [JsonStringEnumMemberName("canceled")]
    Canceled,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

/// <summary>A2A Message — 单条对话消息。</summary>
public sealed class Message
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("parts")]
    public required IReadOnlyList<Part> Parts { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>A2A Artifact — agent 生成的输出制品。</summary>
public sealed class Artifact
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("parts")]
    public required IReadOnlyList<Part> Parts { get; init; }

    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>A2A Part — 消息/制品中的内容分片。按 A2A 协议用 "type" 字段区分。</summary>
[JsonConverter(typeof(PartJsonConverter))]
public abstract class Part
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class TextPart : Part
{
    public override string Type => "text";

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed class FilePart : Part
{
    public override string Type => "file";

    [JsonPropertyName("file")]
    public required FileContent File { get; init; }
}

public sealed class DataPart : Part
{
    public override string Type => "data";

    [JsonPropertyName("data")]
    public required Dictionary<string, object?> Data { get; init; }
}

public sealed class FileContent
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; init; }

    [JsonPropertyName("bytes")]
    public string? Bytes { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

/// <summary>A2A Part 的自定义 JSON 转换器，按 "type" 字段路由到具体子类。</summary>
internal sealed class PartJsonConverter : JsonConverter<Part>
{
    public override Part? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

        return type switch
        {
            "text" => new TextPart
            {
                Text = root.GetProperty("text").GetString() ?? "",
                Metadata = DeserializeMetadata(root),
            },
            "file" => new FilePart
            {
                File = JsonSerializer.Deserialize<FileContent>(root.GetProperty("file").GetRawText(), options)!,
                Metadata = DeserializeMetadata(root),
            },
            "data" => new DataPart
            {
                Data = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                    root.GetProperty("data").GetRawText(), options) ?? [],
                Metadata = DeserializeMetadata(root),
            },
            // 对于未知 type，尝试按内容推断
            _ when root.TryGetProperty("text", out _) => new TextPart
            {
                Text = root.GetProperty("text").GetString() ?? "",
                Metadata = DeserializeMetadata(root),
            },
            _ => throw new JsonException($"Unknown part type: '{type}'"),
        };
    }

    public override void Write(Utf8JsonWriter writer, Part value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);

        switch (value)
        {
            case TextPart textPart:
                writer.WriteString("text", textPart.Text);
                break;
            case FilePart filePart:
                writer.WritePropertyName("file");
                JsonSerializer.Serialize(writer, filePart.File, options);
                break;
            case DataPart dataPart:
                writer.WritePropertyName("data");
                JsonSerializer.Serialize(writer, dataPart.Data, options);
                break;
        }

        if (value.Metadata is { Count: > 0 })
        {
            writer.WritePropertyName("metadata");
            JsonSerializer.Serialize(writer, value.Metadata, options);
        }

        writer.WriteEndObject();
    }

    private static Dictionary<string, string>? DeserializeMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out var metaProp) || metaProp.ValueKind == JsonValueKind.Null)
            return null;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(metaProp.GetRawText());
    }
}
