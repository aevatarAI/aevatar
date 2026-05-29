using Aevatar.AI.ToolProviders.Web;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Application.Responses;

namespace Aevatar.Mainnet.Host.Api.Responses;

// Refactor (iter159/cluster-1215):
//   Old pattern: Application directly used ToolProviders.Web concrete types
//   New principle: Host-side adapter implements IResponsesWebSubstituteBackend
//                  using Aevatar.AI.ToolProviders.Web concrete tools
internal sealed class ResponsesWebSubstituteBackendAdapter : IResponsesWebSubstituteBackend
{
    private readonly IWebApiClient _webClient;
    private readonly WebToolOptions _webOptions;

    public ResponsesWebSubstituteBackendAdapter(
        IWebApiClient webClient,
        WebToolOptions webOptions)
    {
        _webClient = webClient ?? throw new ArgumentNullException(nameof(webClient));
        _webOptions = webOptions ?? throw new ArgumentNullException(nameof(webOptions));
    }

    public int DefaultMaxSearchResults => _webOptions.MaxSearchResults;

    public async Task<ResponsesWebFetchBoundaryResult> ExecuteWebFetchAsync(
        ResponsesWebFetchBoundaryInput input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The URL came from LLM-controlled input. Never forward the caller's
        // NyxID bearer to an arbitrary fetch target.
        var result = await _webClient.FetchUrlAsync(
            token: string.Empty,
            input.Url,
            ct).ConfigureAwait(false);

        return new ResponsesWebFetchBoundaryResult(
            result.OriginalUrl,
            result.StatusCode,
            result.ContentType,
            result.Body ?? string.Empty,
            result.RedirectUrl ?? string.Empty);
    }

    public async Task<ResponsesWebSearchBoundaryResult> ExecuteWebSearchAsync(
        ResponsesWebSearchBoundaryInput input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var result = await _webClient.SearchAsync(
            input.NyxIdAccessToken,
            input.Query,
            input.MaxResults,
            ct).ConfigureAwait(false);
        // Refactor (issue1273/first-slice): Old pattern: Host adapter re-parsed loose
        // provider Value/JSON into Responses output. New principle: provider returns a
        // local typed DTO and Host only maps between boundary-owned typed contracts.
        return new ResponsesWebSearchBoundaryResult(ToSearchOutput(result));
    }

    private static ResponsesWebSearchToolOutput ToSearchOutput(WebSearchResult result)
    {
        var output = new ResponsesWebSearchToolOutput();
        output.Results.AddRange(result.Results
            .Select(static item => new ResponsesWebSearchResultItem
            {
                Title = item.Title,
                Url = item.Url,
                Snippet = item.Snippet,
            }));
        return output;
    }
}
