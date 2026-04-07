using System.Text.Json.Serialization;

namespace Aevatar.Interop.A2A.Abstractions.Models;

/// <summary>A2A Agent Card — describes an agent's capabilities for service discovery.</summary>
public sealed class AgentCard
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0.0";

    [JsonPropertyName("capabilities")]
    public AgentCapabilities Capabilities { get; init; } = new();

    [JsonPropertyName("skills")]
    public IReadOnlyList<AgentSkill> Skills { get; init; } = [];

    [JsonPropertyName("defaultInputModes")]
    public IReadOnlyList<string> DefaultInputModes { get; init; } = ["text"];

    [JsonPropertyName("defaultOutputModes")]
    public IReadOnlyList<string> DefaultOutputModes { get; init; } = ["text"];
}

public sealed class AgentCapabilities
{
    [JsonPropertyName("streaming")]
    public bool Streaming { get; init; }

    [JsonPropertyName("pushNotifications")]
    public bool PushNotifications { get; init; }

    [JsonPropertyName("stateTransitionHistory")]
    public bool StateTransitionHistory { get; init; }
}

public sealed class AgentSkill
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    [JsonPropertyName("examples")]
    public IReadOnlyList<string> Examples { get; init; } = [];
}
