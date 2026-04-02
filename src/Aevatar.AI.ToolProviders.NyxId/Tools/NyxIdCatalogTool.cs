using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to browse the NyxID service catalog.</summary>
public sealed class NyxIdCatalogTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdCatalogTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_catalog";

    public string Description =>
        "Browse available service templates in the NyxID catalog. " +
        "Provide 'slug' to get details for a specific service, or omit to list all.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "slug": {
              "type": "string",
              "description": "Service slug to show details for (e.g. 'llm-openai'). Omit to list all."
            }
          }
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var args = ToolArgs.Parse(argumentsJson);
        var slug = args.Str("slug");

        if (!string.IsNullOrWhiteSpace(slug))
            return await _client.GetCatalogEntryAsync(token, slug, ct);

        return await _client.ListCatalogAsync(token, ct);
    }
}
