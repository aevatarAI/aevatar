using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Tools;

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

    public ToolPresentationDescriptor Presentation =>
        ToolPresentationDescriptors.BuiltIn(Name, "Web fetch", Description);

    public bool IsReadOnly => true;

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        if (WebToolResultBoundaryJson.TryReadError(resultJson, out var error) && error != null)
        {
            var code = error.Code == "host_resolution_failed"
                ? "WEB_FETCH_DNS_FAILURE"
                : IsOwnedFailureCode(error.Code)
                    ? error.Code
                    : "WEB_FETCH_URL_REJECTED";
            return Receipt(
                callId,
                toolName,
                AgentToolReceiptStatus.Error,
                resultJson,
                code,
                error.Message);
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            var isFetchSuccess = root.TryGetProperty("status_code", out var statusCode) &&
                                 statusCode.TryGetInt32(out var value) &&
                                 value is >= 200 and <= 299;
            var isRedirect = root.TryGetProperty("status", out var status) &&
                             string.Equals(status.GetString(), "redirect", StringComparison.Ordinal);
            return isFetchSuccess || isRedirect
                ? Receipt(callId, toolName, AgentToolReceiptStatus.Success, resultJson)
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var args = ToolArgs.Parse(argumentsJson);
        var url = args.Str("url");
        if (string.IsNullOrWhiteSpace(url))
            return """{"error":"'url' is required"}""";

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url[7..];
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        var validation = await WebFetchUrlGuard.ValidateResolvedAsync(url, ct);
        if (!validation.IsAllowed)
        {
            var code = validation.RejectionCode ?? "url_rejected";
            return WebToolResultBoundaryJson.ToBoundaryJson(new WebToolError(code, code));
        }

        // The URL is LLM/user-controlled. Do not forward the caller's NyxID bearer
        // to arbitrary hosts; authenticated downstream access must use a typed
        // NyxID proxy tool instead.
        var result = await _client.FetchUrlAsync(string.Empty, validation.NormalizedUrl!, ct);
        if (result.Error != null)
            return WebToolResultBoundaryJson.ToBoundaryJson(result.Error);

        if (result.RedirectUrl != null)
        {
            return WebToolResultBoundaryJson.ToBoundaryJson(new WebFetchRedirectResult(
                result.OriginalUrl,
                result.RedirectUrl,
                "The URL redirected to a different host. Fetch the redirect_url to get the content."));
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

        return WebToolResultBoundaryJson.ToBoundaryJson(new WebFetchToolResult(
            result.OriginalUrl,
            result.StatusCode,
            result.ContentType,
            body,
            truncated));
    }

    private AgentToolReceipt Receipt(
        string callId,
        string toolName,
        AgentToolReceiptStatus status,
        string resultJson,
        string errorCode = "",
        string errorMessage = "") =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = status,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            ErrorCode = errorCode ?? string.Empty,
            ErrorMessage = errorMessage ?? string.Empty,
            ResultJson = resultJson ?? string.Empty,
        };

    private static bool IsOwnedFailureCode(string code) =>
        code is "WEB_FETCH_DNS_FAILURE" or
            "WEB_FETCH_TLS_FAILURE" or
            "WEB_FETCH_TIMEOUT" or
            "WEB_FETCH_TRANSPORT_FAILURE" ||
        code.StartsWith("WEB_FETCH_HTTP_", StringComparison.Ordinal);

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
