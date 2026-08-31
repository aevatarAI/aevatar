using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.ToolProviders.NyxId;

namespace Aevatar.AI.ToolProviders.Lark;

public sealed class LarkKnowledgeNyxClient(
    LarkToolOptions options,
    NyxIdApiClient nyxClient) : ILarkKnowledgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Task<string> SearchAsync(
        string token,
        LarkKnowledgeSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new Dictionary<string, object?>
        {
            ["query"] = request.Query,
            ["page_size"] = request.PageSize,
        };

        if (request.SpaceIds.Count == 0)
        {
            body["doc_filter"] = CreateResourceFilter();
            body["wiki_filter"] = CreateResourceFilter();
        }
        else
        {
            var wikiFilter = CreateResourceFilter();
            wikiFilter["space_ids"] = request.SpaceIds;
            body["wiki_filter"] = wikiFilter;
        }

        return nyxClient.ProxyRequestAsync(
            token,
            options.ProviderSlug,
            "open-apis/search/v2/doc_wiki/search",
            "POST",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            cancellationToken);
    }

    public Task<string> ResolveWikiNodeAsync(
        string token,
        string wikiToken,
        CancellationToken cancellationToken) =>
        nyxClient.ProxyRequestAsync(
            token,
            options.ProviderSlug,
            $"open-apis/wiki/v2/spaces/get_node?token={Uri.EscapeDataString(wikiToken)}",
            "GET",
            body: null,
            extraHeaders: null,
            cancellationToken);

    public Task<string> ReadDocxRawContentAsync(
        string token,
        string documentToken,
        CancellationToken cancellationToken) =>
        nyxClient.ProxyRequestAsync(
            token,
            options.ProviderSlug,
            $"open-apis/docx/v1/documents/{Uri.EscapeDataString(documentToken)}/raw_content",
            "GET",
            body: null,
            extraHeaders: null,
            cancellationToken);

    private static Dictionary<string, object?> CreateResourceFilter() => new()
    {
        ["doc_types"] = new[] { "DOCX", "WIKI" },
    };
}
