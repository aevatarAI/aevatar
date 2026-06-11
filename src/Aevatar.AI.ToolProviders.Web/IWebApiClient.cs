namespace Aevatar.AI.ToolProviders.Web;

/// <summary>
/// Abstraction over <see cref="WebApiClient"/> so consumers depend on the
/// contract instead of the concrete HTTP client. Lets host code follow the
/// dependency-inversion rule from CLAUDE.md and lets tests substitute a
/// stub without spinning up a real <see cref="HttpClient"/>.
/// </summary>
public interface IWebApiClient
{
    /// <summary>Perform a web search via NyxID proxy or a direct API.</summary>
    Task<WebSearchResult> SearchAsync(string token, string query, int maxResults, CancellationToken ct);

    /// <summary>Fetch a URL and return the response body as text.</summary>
    /// <param name="token">
    /// Optional bearer token. Callers that route through arbitrary,
    /// LLM-controlled URLs MUST pass empty so the token is never forwarded to
    /// attacker-controlled hosts.
    /// </param>
    Task<WebFetchResult> FetchUrlAsync(string token, string url, CancellationToken ct);
}
