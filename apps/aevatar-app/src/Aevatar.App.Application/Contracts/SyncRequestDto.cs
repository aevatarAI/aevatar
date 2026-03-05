using System.Text.Json.Serialization;

namespace Aevatar.App.Application.Contracts;

public sealed class SyncRequestDto
{
    [JsonPropertyName("syncId")]
    public required string SyncId { get; init; }

    [JsonPropertyName("clientRevision")]
    public int ClientRevision { get; init; }

    [JsonPropertyName("entities")]
    public Dictionary<string, Dictionary<string, EntityDto>> Entities { get; init; } = new();
}
