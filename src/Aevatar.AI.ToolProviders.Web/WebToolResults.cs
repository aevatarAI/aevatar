using System.Text.Json;

namespace Aevatar.AI.ToolProviders.Web;

public sealed record WebSearchResult(
    IReadOnlyList<WebSearchResultItem> Results,
    WebToolError? Error = null)
{
    public static WebSearchResult Empty { get; } = new(Array.Empty<WebSearchResultItem>());
}

public sealed record WebSearchResultItem(
    string Title,
    string Url,
    string Snippet);

public sealed record WebToolError(
    string Code,
    string Message);

/// <summary>Result of a URL fetch operation.</summary>
public sealed record WebFetchResult(
    int StatusCode,
    string ContentType,
    string? Body,
    string? RedirectUrl,
    string OriginalUrl,
    WebToolError? Error = null);

public sealed record WebFetchToolResult(
    string Url,
    int StatusCode,
    string ContentType,
    string Content,
    bool Truncated);

public sealed record WebFetchRedirectResult(
    string OriginalUrl,
    string RedirectUrl,
    string Message);

// Refactor (issue1273/first-slice): Old pattern: Web provider and Host adapter each converted
// loose JSON/Value search payloads independently. New principle: Web provider owns a local
// typed DTO and one shared boundary JSON mapper at the provider/Host adapter boundary.
public static class WebToolResultBoundaryJson
{
    public static WebSearchResult ParseSearchPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return WebSearchResult.Empty;

        using var document = TryParseDocument(payload);
        if (document == null)
        {
            return new WebSearchResult(
                Array.Empty<WebSearchResultItem>(),
                new WebToolError("unstructured_search_result", payload));
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new WebSearchResult(
                Array.Empty<WebSearchResultItem>(),
                new WebToolError("unstructured_search_result", root.ToString()));
        }

        if (root.TryGetProperty("error", out var error))
        {
            var code = ReadString(error);
            var message = root.TryGetProperty("message", out var messageValue)
                ? ReadString(messageValue)
                : code;
            return new WebSearchResult(
                Array.Empty<WebSearchResultItem>(),
                new WebToolError(code, message));
        }

        if (!root.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return WebSearchResult.Empty;
        }

        return new WebSearchResult(results
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => new WebSearchResultItem(
                ReadPropertyString(item, "title"),
                ReadPropertyString(item, "url"),
                ReadPropertyString(item, "snippet")))
            .ToArray());
    }

    public static string ToBoundaryJson(WebSearchResult? result)
    {
        if (result?.Error != null)
            return ToBoundaryJson(result.Error);

        return JsonSerializer.Serialize(new
        {
            results = result?.Results.Select(static item => new
            {
                title = item.Title,
                url = item.Url,
                snippet = item.Snippet,
            }) ?? Enumerable.Empty<object>(),
        });
    }

    public static string ToBoundaryJson(WebFetchResult result)
    {
        if (result.Error != null)
            return ToBoundaryJson(result.Error);

        var body = result.Body ?? string.Empty;
        var fields = new Dictionary<string, object?>
        {
            ["url"] = result.OriginalUrl,
            ["status_code"] = result.StatusCode,
            ["content_type"] = result.ContentType,
            ["content"] = body,
            ["truncated"] = false,
        };
        if (!string.IsNullOrWhiteSpace(result.RedirectUrl))
            fields["redirect_url"] = result.RedirectUrl;

        return JsonSerializer.Serialize(fields);
    }

    public static WebFetchResult ParseFetchPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return EmptyFetchResult();

        using var document = TryParseDocument(payload);
        if (document == null)
            return EmptyFetchResult();

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return EmptyFetchResult();

        return new WebFetchResult(
            ReadPropertyInt32(root, "status_code"),
            ReadPropertyString(root, "content_type"),
            ReadPropertyString(root, "content"),
            ReadOptionalPropertyString(root, "redirect_url"),
            ReadPropertyString(root, "url"),
            TryReadError(root));
    }

    public static bool TryReadError(string? payload, out WebToolError? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        using var document = TryParseDocument(payload);
        if (document == null || document.RootElement.ValueKind != JsonValueKind.Object)
            return false;

        error = TryReadError(document.RootElement);
        return error != null;
    }

    public static string ToBoundaryJson(WebFetchToolResult result) =>
        JsonSerializer.Serialize(new
        {
            url = result.Url,
            status_code = result.StatusCode,
            content_type = result.ContentType,
            content = result.Content,
            truncated = result.Truncated,
        });

    public static string ToBoundaryJson(WebFetchRedirectResult result) =>
        JsonSerializer.Serialize(new
        {
            status = "redirect",
            original_url = result.OriginalUrl,
            redirect_url = result.RedirectUrl,
            message = result.Message,
        });

    public static string ToBoundaryJson(WebToolError error) =>
        JsonSerializer.Serialize(new
        {
            error = error.Code,
            message = error.Message,
        });

    private static JsonDocument? TryParseDocument(string payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadPropertyString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value)
            ? ReadString(value)
            : string.Empty;

    private static string? ReadOptionalPropertyString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? ReadString(value)
            : null;

    private static int ReadPropertyInt32(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
            return intValue;

        return 0;
    }

    private static string ReadString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();

    private static WebToolError? TryReadError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var errorValue))
            return null;

        var code = ReadString(errorValue);
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var message = root.TryGetProperty("message", out var messageValue)
            ? ReadString(messageValue)
            : code;
        return new WebToolError(code, message);
    }

    private static WebFetchResult EmptyFetchResult() =>
        new(0, string.Empty, string.Empty, null, string.Empty);
}
