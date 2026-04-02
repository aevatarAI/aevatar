using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Web.Tools;

/// <summary>
/// Searches the web for real-time information.
/// Routes through NyxID proxy or a direct search API backend.
/// </summary>
public sealed class WebSearchTool : IAgentTool
{
    private readonly WebApiClient _client;
    private readonly WebToolOptions _options;

    public WebSearchTool(WebApiClient client, WebToolOptions options)
    {
        _client = client;
        _options = options;
    }

    public string Name => "web_search";

    public string Description =>
        "Search the web for current information, documentation, or answers. " +
        "Returns search results with titles, snippets, and URLs. " +
        "Use this when you need up-to-date information beyond your training data.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "The search query to execute"
            },
            "max_results": {
              "type": "integer",
              "description": "Maximum number of results to return (default: 10, max: 20)"
            }
          },
          "required": ["query"]
        }
        """;

    public bool IsReadOnly => true;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var args = ToolArgs.Parse(argumentsJson);
        var query = args.Str("query");
        if (string.IsNullOrWhiteSpace(query))
            return """{"error":"'query' is required"}""";

        var maxResults = Math.Clamp(args.Int("max_results") ?? _options.MaxSearchResults, 1, 20);
        return await _client.SearchAsync(token, query, maxResults, ct);
    }
}
