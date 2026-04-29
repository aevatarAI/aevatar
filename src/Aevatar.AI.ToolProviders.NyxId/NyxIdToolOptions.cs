namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>NyxID tool provider configuration.</summary>
public sealed class NyxIdToolOptions
{
    /// <summary>NyxID API base URL (e.g. https://nyx-api.chrono-ai.fun).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Bearer token used by <see cref="NyxIdSpecCatalog"/> to fetch
    /// <c>{BaseUrl}/api/v1/docs/openapi.json</c>. NyxID enforces this endpoint
    /// as human-only (rejects service-account and delegated tokens), so this
    /// must be a real user's API key or access token. When unset the catalog
    /// stays empty and the background refresh is skipped — generic capability
    /// discovery (<c>nyxid_search_capabilities</c>, <c>nyxid_proxy</c>) is
    /// unavailable but specialized NyxID tools continue to work.
    /// </summary>
    public string? SpecFetchToken { get; set; }
}
