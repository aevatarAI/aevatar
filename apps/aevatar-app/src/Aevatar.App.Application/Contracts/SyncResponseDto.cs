using System.Text.Json.Serialization;

namespace Aevatar.App.Application.Contracts;

public sealed class SyncResponseDto
{
    [JsonPropertyName("syncId")]
    public required string SyncId { get; init; }

    [JsonPropertyName("serverRevision")]
    public int ServerRevision { get; init; }

    [JsonPropertyName("entities")]
    public Dictionary<string, Dictionary<string, EntityDto>> Entities { get; init; } = new();

    [JsonPropertyName("accepted")]
    public List<string> Accepted { get; init; } = [];

    [JsonPropertyName("rejected")]
    public List<RejectedEntityDto> Rejected { get; init; } = [];
}

public sealed class RejectedEntityDto
{
    [JsonPropertyName("clientId")]
    public required string ClientId { get; init; }

    [JsonPropertyName("serverRevision")]
    public int ServerRevision { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}
