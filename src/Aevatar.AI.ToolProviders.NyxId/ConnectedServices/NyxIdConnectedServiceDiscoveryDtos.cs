using System.Text.Json.Serialization;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

internal sealed class NyxIdConnectedServiceListDto
{
    [JsonPropertyName("keys")]
    public List<NyxIdConnectedServiceDto> Keys { get; init; } = [];
}

internal sealed class NyxIdConnectedServiceDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("catalog_service_slug")]
    public string? CatalogServiceSlug { get; init; }

    [JsonPropertyName("catalog_service_name")]
    public string? CatalogServiceName { get; init; }

    [JsonPropertyName("connected")]
    public bool? Connected { get; init; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; }

    [JsonPropertyName("allowed")]
    public bool? Allowed { get; init; }

    [JsonPropertyName("credential_source")]
    public NyxIdCredentialSourceDto? CredentialSource { get; init; }
}

internal sealed class NyxIdCredentialSourceDto
{
    [JsonPropertyName("allowed")]
    public bool? Allowed { get; init; }
}

internal sealed class NyxIdCatalogListDto
{
    [JsonPropertyName("entries")]
    public List<NyxIdCatalogEntryDto> Entries { get; init; } = [];
}

internal sealed class NyxIdCatalogEntryDto
{
    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; init; }
}
