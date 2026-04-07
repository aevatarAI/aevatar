namespace Aevatar.AI.ToolProviders.Web;

/// <summary>Configuration for web search and fetch tools.</summary>
public sealed class WebToolOptions
{
    /// <summary>
    /// NyxID proxy slug for the web search backend (e.g. "api-web-search").
    /// When set, search requests go through NyxID proxy with auto-injected credentials.
    /// When null, a direct SearchApiBaseUrl must be configured.
    /// </summary>
    public string? NyxIdSearchSlug { get; set; }

    /// <summary>
    /// Direct base URL for a search API (used when NyxIdSearchSlug is not set).
    /// </summary>
    public string? SearchApiBaseUrl { get; set; }

    /// <summary>NyxID API base URL for proxy requests.</summary>
    public string? NyxIdBaseUrl { get; set; }

    /// <summary>Maximum number of search results to return (default: 10).</summary>
    public int MaxSearchResults { get; set; } = 10;

    /// <summary>Default timeout in seconds for web fetch requests (default: 30).</summary>
    public int FetchTimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum response body size in bytes for web fetch (default: 512 KB).</summary>
    public int MaxFetchBodyBytes { get; set; } = 512 * 1024;
}
