using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.App.Application.Contracts;

public sealed class EntityDto
{
    [JsonPropertyName("clientId")]
    public required string ClientId { get; init; }

    [JsonPropertyName("entityType")]
    public required string EntityType { get; init; }

    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    [JsonPropertyName("revision")]
    public int Revision { get; init; }

    [JsonPropertyName("refs")]
    public Dictionary<string, string> Refs { get; init; } = new();

    [JsonPropertyName("position")]
    public int Position { get; init; }

    [JsonPropertyName("inputs")]
    public JsonElement? Inputs { get; init; }

    [JsonPropertyName("output")]
    public JsonElement? Output { get; init; }

    [JsonPropertyName("state")]
    public JsonElement? State { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("bankEligible")]
    public bool BankEligible { get; init; }

    [JsonPropertyName("bankHash")]
    public string? BankHash { get; init; }

    [JsonPropertyName("deletedAt")]
    public string? DeletedAt { get; init; }

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; init; }
}
