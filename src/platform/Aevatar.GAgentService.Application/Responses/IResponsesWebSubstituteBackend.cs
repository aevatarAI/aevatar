using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Application.Responses;

// Refactor (iter159/cluster-1215):
//   Old pattern: Application directly depended on concrete Aevatar.AI.ToolProviders.Web for WebFetch/WebSearch IO
//   New principle: Application defines IResponsesWebSubstituteBackend port;
//                  Host binds concrete Web provider as implementation;
//                  Application has no compile-time dependency on ToolProviders.Web
public interface IResponsesWebSubstituteBackend
{
    Task<ResponsesWebFetchBoundaryResult> ExecuteWebFetchAsync(
        ResponsesWebFetchBoundaryInput input,
        CancellationToken ct);

    Task<ResponsesWebSearchBoundaryResult> ExecuteWebSearchAsync(
        ResponsesWebSearchBoundaryInput input,
        CancellationToken ct);

    int DefaultMaxSearchResults { get; }
}

public sealed record ResponsesWebFetchBoundaryInput(
    string Url,
    string ExtractHint);

public sealed record ResponsesWebFetchBoundaryResult(
    string Url,
    int StatusCode,
    string ContentType,
    string Content,
    string RedirectUrl);

public sealed record ResponsesWebSearchBoundaryInput(
    string Query,
    int MaxResults,
    string NyxIdAccessToken);

// Refactor (iter161-cluster-001 #1251-first):
//   Old pattern: Host returned untyped provider Value for Application to interpret.
//   New principle: Host returns the Responses-owned typed Web search output.
public sealed record ResponsesWebSearchBoundaryResult(
    ResponsesWebSearchToolOutput Output);
