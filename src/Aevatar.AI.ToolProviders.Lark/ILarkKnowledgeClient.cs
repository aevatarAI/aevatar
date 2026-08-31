namespace Aevatar.AI.ToolProviders.Lark;

public interface ILarkKnowledgeClient
{
    Task<string> SearchAsync(
        string token,
        LarkKnowledgeSearchRequest request,
        CancellationToken cancellationToken);

    Task<string> ResolveWikiNodeAsync(
        string token,
        string wikiToken,
        CancellationToken cancellationToken);

    Task<string> ReadDocxRawContentAsync(
        string token,
        string documentToken,
        CancellationToken cancellationToken);
}

public sealed record LarkKnowledgeSearchRequest(
    string Query,
    int PageSize,
    IReadOnlyList<string> SpaceIds);
