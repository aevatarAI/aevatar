using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Web.Tools;

/// <summary>
/// Fetches content from a URL and returns it as text.
/// Strips HTML to plain text for easier consumption by the LLM.
/// </summary>
public sealed class WebFetchTool : IAgentTool
{
    private readonly WebApiClient _client;

    public WebFetchTool(WebApiClient client) => _client = client;

    public string Name => "web_fetch";

    public string Description =>
        "Fetch content from a URL and return it as text. " +
        "HTML pages are stripped to plain text. " +
        "Use this to read web pages, API responses, or documentation found via web_search. " +
        "If the URL redirects to a different host, the redirect URL is returned instead.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "url": {
              "type": "string",
              "description": "The URL to fetch content from. Must be a fully-formed valid URL."
            },
            "extract_hint": {
              "type": "string",
              "description": "Optional hint for what information to focus on when reading the page"
            }
          },
          "required": ["url"]
        }
        """;

    public bool IsReadOnly => true;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken) ?? "";

        var args = ToolArgs.Parse(argumentsJson);
        var url = args.Str("url");
        if (string.IsNullOrWhiteSpace(url))
            return """{"error":"'url' is required"}""";

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url[7..];
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        var result = await _client.FetchUrlAsync(token, url, ct);

        if (result.RedirectUrl != null)
        {
            return JsonSerializer.Serialize(new
            {
                status = "redirect",
                original_url = result.OriginalUrl,
                redirect_url = result.RedirectUrl,
                message = "The URL redirected to a different host. Fetch the redirect_url to get the content.",
            });
        }

        var body = result.Body ?? "";

        if (result.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
            body.TrimStart().StartsWith('<'))
        {
            body = HtmlToPlainText(body);
        }

        const int maxChars = 50_000;
        var truncated = body.Length > maxChars;
        if (truncated)
            body = body[..maxChars];

        return JsonSerializer.Serialize(new
        {
            url = result.OriginalUrl,
            status_code = result.StatusCode,
            content_type = result.ContentType,
            content = body,
            truncated,
        });
    }

    private static string HtmlToPlainText(string html)
    {
        var text = Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(br|/p|/div|/li|/tr|/h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = text
            .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
            .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&nbsp;", " ");
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }
}
