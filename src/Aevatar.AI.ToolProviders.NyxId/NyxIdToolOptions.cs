namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>NyxID tool provider configuration.</summary>
public sealed class NyxIdToolOptions
{
    /// <summary>NyxID API base URL (e.g. https://nyx-api.chrono-ai.fun).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Optional token for fetching the OpenAPI spec from NyxID.
    /// Used by NyxIdSpecCatalog when the spec endpoint requires auth.
    /// If null, the spec fetch is attempted without auth (public endpoint).
    /// </summary>
    public string? SpecFetchToken { get; set; }
}
