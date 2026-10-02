namespace Aevatar.AI.ToolProviders.NyxId;

public sealed partial class NyxIdApiClient
{
    internal bool IsPublicApiOrigin(Uri documentUri) =>
        Uri.TryCreate(_options.EffectiveApiBaseUrl, UriKind.Absolute, out var apiUri) &&
        string.Equals(documentUri.Scheme, apiUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(documentUri.Host, apiUri.Host, StringComparison.OrdinalIgnoreCase) &&
        documentUri.Port == apiUri.Port;

    /// <summary>
    /// Reads NyxID's document resource, not a path on the downstream service.
    /// The caller must be authorized to read the selected service identity.
    /// </summary>
    internal Task<NyxIdProxyTextResponse> GetServiceOpenApiDocumentAsync(
        string accessToken,
        string serviceId,
        long maxBytes,
        CancellationToken ct) =>
        GetBoundedAsync(accessToken, BuildServiceOpenApiDocumentPath(serviceId), maxBytes, ct);

    internal static string BuildServiceOpenApiDocumentPath(string serviceId) =>
        $"/api/v1/proxy/services/{Uri.EscapeDataString(serviceId)}/openapi.json";
}
