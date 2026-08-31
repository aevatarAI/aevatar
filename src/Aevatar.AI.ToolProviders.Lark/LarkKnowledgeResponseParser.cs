using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aevatar.AI.ToolProviders.Lark;

internal static class LarkKnowledgeResponseParser
{
    private static readonly Regex HighlightTagPattern = new(
        "<[^>]+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static LarkKnowledgeSearchResult ParseSearch(string response)
    {
        EnsureSuccess(response);
        using var document = JsonDocument.Parse(response);
        var data = ResolveDataRoot(document.RootElement);
        var candidates = new List<LarkKnowledgeCandidate>();

        if (data.TryGetProperty("res_units", out var resultUnits) &&
            resultUnits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in resultUnits.EnumerateArray())
            {
                if (TryParseCandidate(item) is { } candidate)
                    candidates.Add(candidate);
            }
        }

        return new LarkKnowledgeSearchResult(
            candidates,
            TryReadBool(data, "has_more") ?? false,
            TryReadString(data, "page_token"));
    }

    public static LarkWikiNodeResult ParseWikiNode(string response)
    {
        EnsureSuccess(response);
        using var document = JsonDocument.Parse(response);
        var data = ResolveDataRoot(document.RootElement);
        var node = data.TryGetProperty("node", out var nodeProperty) &&
                   nodeProperty.ValueKind == JsonValueKind.Object
            ? nodeProperty
            : data;
        var objectType = TryReadString(node, "obj_type");
        var objectToken = TryReadString(node, "obj_token");

        if (string.IsNullOrWhiteSpace(objectType))
            throw new InvalidOperationException("missing_wiki_object_type");
        if (string.IsNullOrWhiteSpace(objectToken))
            throw new InvalidOperationException("missing_wiki_object_token");

        return new LarkWikiNodeResult(objectType.ToLowerInvariant(), objectToken);
    }

    public static string ParseDocxRawContent(string response)
    {
        EnsureSuccess(response);
        using var document = JsonDocument.Parse(response);
        var content = TryReadString(ResolveDataRoot(document.RootElement), "content");
        return content ?? throw new InvalidOperationException("missing_docx_content");
    }

    private static LarkKnowledgeCandidate? TryParseCandidate(JsonElement item)
    {
        var entityType = TryReadString(item, "entity_type")?.ToUpperInvariant();
        if (entityType is not ("DOCX" or "WIKI"))
            return null;

        var resultData = item.TryGetProperty("result_meta", out var resultMeta) &&
                         resultMeta.ValueKind == JsonValueKind.Object
            ? resultMeta
            : item;
        var resourceToken = TryReadString(resultData, "token") ??
                            TryReadString(item, "token");
        if (string.IsNullOrWhiteSpace(resourceToken))
            return null;

        var sourceKind = entityType.ToLowerInvariant();
        var title = NormalizeTitle(
            TryReadString(item, "title") ??
            TryReadString(item, "title_highlighted") ??
            string.Empty);
        var url = TryReadString(resultData, "url") ??
                  TryReadString(item, "url") ??
                  string.Empty;
        var documentToken = sourceKind == "docx"
            ? resourceToken
            : TryReadString(resultData, "obj_token") ??
              TryReadString(resultData, "document_token") ??
              TryReadString(item, "obj_token");

        return new LarkKnowledgeCandidate(
            sourceKind,
            title,
            url,
            resourceToken,
            documentToken);
    }

    private static void EnsureSuccess(string? response)
    {
        if (!LarkProxyResponseParser.TryParseError(response, out var error))
            return;

        if (error.StartsWith("nyx_proxy_error", StringComparison.Ordinal))
        {
            var messageIndex = error.IndexOf(" message=", StringComparison.Ordinal);
            var bodyIndex = error.IndexOf(" body=", StringComparison.Ordinal);
            var detailIndex = new[] { messageIndex, bodyIndex }
                .Where(static index => index >= 0)
                .DefaultIfEmpty(error.Length)
                .Min();
            error = error[..detailIndex];
        }

        throw new InvalidOperationException(error);
    }

    private static string NormalizeTitle(string title) =>
        WebUtility.HtmlDecode(HighlightTagPattern.Replace(title, string.Empty)).Trim();

    private static JsonElement ResolveDataRoot(JsonElement root) =>
        root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data
            : root;

    private static string? TryReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool? TryReadBool(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;
}

internal sealed record LarkKnowledgeSearchResult(
    IReadOnlyList<LarkKnowledgeCandidate> Candidates,
    bool HasMore,
    string? PageToken);

internal sealed record LarkKnowledgeCandidate(
    string SourceKind,
    string Title,
    string Url,
    string ResourceToken,
    string? DocumentToken);

internal sealed record LarkWikiNodeResult(
    string ObjectType,
    string ObjectToken);
