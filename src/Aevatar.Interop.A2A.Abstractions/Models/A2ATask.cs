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

/// <summary>A2A Part — 消息/制品中的内容分片。</summary>
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(FilePart), "file")]
[JsonDerivedType(typeof(DataPart), "data")]
public abstract class Part
{
    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class TextPart : Part
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed class FilePart : Part
{
    [JsonPropertyName("file")]
    public required FileContent File { get; init; }
}

public sealed class DataPart : Part
{
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
