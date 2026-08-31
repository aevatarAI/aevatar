using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkDocsSearchTool(ILarkKnowledgeClient client)
    : AgentToolBase<LarkDocsSearchTool.Parameters>
{
    internal const int DefaultMaxSources = 5;
    internal const int MaximumMaxSources = 10;
    internal const int MaximumQueryRunes = 30;
    internal const int MaximumSourceCharacters = 12_000;
    internal const int MaximumTotalCharacters = 48_000;

    public override string Name => "lark_docs_search";

    public override string Description =>
        "Search and read Docs and Wiki pages visible through the current caller's connected Lark account. " +
        "Returns bounded document evidence with source titles and links for grounded answers or extraction.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;
    public override bool IsReadOnly => true;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return SerializeFailure(
                "No caller Lark credential is available; connect or reauthenticate Lark and try again.");
        }

        var query = parameters.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return SerializeFailure("query is required.");

        var maxSources = parameters.MaxSources ?? DefaultMaxSources;
        if (maxSources is < 1 or > MaximumMaxSources)
            return SerializeFailure($"max_sources must be between 1 and {MaximumMaxSources}.");

        var normalizedQuery = TruncateRunes(query, MaximumQueryRunes);
        var spaceIds = NormalizeSpaceIds(parameters.SpaceIds);
        LarkKnowledgeSearchResult searchResult;
        try
        {
            var response = await client.SearchAsync(
                token,
                new LarkKnowledgeSearchRequest(normalizedQuery, maxSources, spaceIds),
                ct);
            searchResult = LarkKnowledgeResponseParser.ParseSearch(response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException exception)
        {
            return SerializeFailure(exception.Message, normalizedQuery);
        }
        catch (Exception)
        {
            return SerializeFailure("lark_docs_search_failed", normalizedQuery);
        }

        var sources = new List<LarkKnowledgeEvidenceSource>();
        var unreadableSources = new List<LarkUnreadableKnowledgeSource>();
        var seenResources = new HashSet<string>(StringComparer.Ordinal);
        var seenDocuments = new HashSet<string>(StringComparer.Ordinal);
        var totalCharacters = 0;

        foreach (var candidate in searchResult.Candidates)
        {
            if (sources.Count >= maxSources || totalCharacters >= MaximumTotalCharacters)
                break;

            var resourceKey = $"{candidate.SourceKind}:{candidate.ResourceToken}";
            if (!seenResources.Add(resourceKey))
                continue;

            try
            {
                var documentToken = await ResolveDocumentTokenAsync(token, candidate, ct);
                if (!seenDocuments.Add(documentToken))
                    continue;

                var response = await client.ReadDocxRawContentAsync(token, documentToken, ct);
                var content = LarkKnowledgeResponseParser.ParseDocxRawContent(response);
                var remainingCharacters = MaximumTotalCharacters - totalCharacters;
                var contentLimit = Math.Min(MaximumSourceCharacters, remainingCharacters);
                var boundedContent = TruncateContent(content, contentLimit);

                sources.Add(new LarkKnowledgeEvidenceSource(
                    SourceId(candidate),
                    candidate.SourceKind,
                    candidate.Title,
                    candidate.Url,
                    documentToken,
                    boundedContent.Content,
                    boundedContent.Truncated));
                totalCharacters += boundedContent.RuneCount;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidOperationException exception)
            {
                unreadableSources.Add(ToUnreadable(candidate, exception.Message));
            }
            catch (Exception)
            {
                unreadableSources.Add(ToUnreadable(candidate, "source_read_failed"));
            }
        }

        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            query = normalizedQuery,
            has_more = searchResult.HasMore,
            sources,
            unreadable_sources = unreadableSources,
        });
    }

    private async Task<string> ResolveDocumentTokenAsync(
        string token,
        LarkKnowledgeCandidate candidate,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(candidate.DocumentToken))
            return candidate.DocumentToken;

        if (candidate.SourceKind != "wiki")
            throw new InvalidOperationException("missing_document_token");

        var response = await client.ResolveWikiNodeAsync(token, candidate.ResourceToken, ct);
        var node = LarkKnowledgeResponseParser.ParseWikiNode(response);
        if (node.ObjectType != "docx")
            throw new InvalidOperationException("unsupported_wiki_object_type");
        return node.ObjectToken;
    }

    private static IReadOnlyList<string> NormalizeSpaceIds(IEnumerable<string>? spaceIds) =>
        spaceIds?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

    private static string TruncateRunes(string value, int maximumRunes)
    {
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count++ >= maximumRunes)
                break;
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    private static (string Content, int RuneCount, bool Truncated) TruncateContent(
        string content,
        int maximumRunes)
    {
        var builder = new StringBuilder();
        var runeCount = 0;
        foreach (var rune in content.EnumerateRunes())
        {
            if (runeCount >= maximumRunes)
                return (builder.ToString(), runeCount, true);

            builder.Append(rune.ToString());
            runeCount++;
        }

        return (builder.ToString(), runeCount, false);
    }

    private static string SerializeFailure(string error, string? query = null) =>
        LarkProxyResponseParser.Serialize(new
        {
            success = false,
            query,
            error,
            sources = Array.Empty<object>(),
            unreadable_sources = Array.Empty<object>(),
        });

    private static string SourceId(LarkKnowledgeCandidate candidate) =>
        $"lark:{candidate.SourceKind}:{candidate.ResourceToken}";

    private static LarkUnreadableKnowledgeSource ToUnreadable(
        LarkKnowledgeCandidate candidate,
        string error) =>
        new(
            SourceId(candidate),
            candidate.SourceKind,
            candidate.Title,
            candidate.Url,
            error);

    public sealed class Parameters
    {
        public string? Query { get; set; }
        public int? MaxSources { get; set; }
        public List<string>? SpaceIds { get; set; }
    }
}

internal sealed record LarkKnowledgeEvidenceSource(
    string SourceId,
    string SourceKind,
    string Title,
    string Url,
    string DocumentToken,
    string Content,
    bool ContentTruncated);

internal sealed record LarkUnreadableKnowledgeSource(
    string SourceId,
    string SourceKind,
    string Title,
    string Url,
    string Error);
