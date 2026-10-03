namespace Aevatar.AI.ToolProviders.NyxId;

public sealed partial class NyxIdApiClient
{
    internal Task<NyxIdProxyTextResponse> ReadCatalogMetadataAsync(string token, string id, CancellationToken ct) =>
        GetBoundedAsync(token, $"/api/v1/keys/{Uri.EscapeDataString(id)}", 1024 * 1024, ct);

    internal Task<NyxIdProxyTextResponse> ReadCatalogSkillRecommendationsAsync(string token, string id, CancellationToken ct) =>
        GetBoundedAsync(token, $"/api/v1/catalog-curation/services/{Uri.EscapeDataString(id)}/skills", 1024 * 1024, ct);

    internal Task<NyxIdProxyTextResponse> ReadCatalogSkillOpenApiAsync(string token, string id, CancellationToken ct) =>
        GetBoundedAsync(token, $"/api/v1/catalog-curation/services/{Uri.EscapeDataString(id)}/openapi.json", 4 * 1024 * 1024, ct);

    internal Task<NyxIdProxyTextResponse> WriteCatalogSkillRecommendationsAsync(string token, string id, string body, CancellationToken ct) =>
        PutTextResponseAsync(token, $"/api/v1/catalog-curation/services/{Uri.EscapeDataString(id)}/skills", body, ct);
}
