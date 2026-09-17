using Aevatar.AI.ToolProviders.Web;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.Mainnet.Host.Api.Responses;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Capabilities.Tests.Responses;

public sealed class ResponsesWebSubstituteBackendAdapterTests
{
    [Fact]
    public async Task ExecuteWebFetchAsync_ShouldNotForwardNyxIdTokenAndMapFetchResult()
    {
        var webClient = new RecordingWebApiClient
        {
            FetchResult = new WebFetchResult(
                200,
                "text/plain",
                "fresh body",
                "https://example.com/final",
                "https://example.com/docs"),
        };
        var adapter = new ResponsesWebSubstituteBackendAdapter(
            webClient,
            new WebToolOptions { MaxSearchResults = 7 });

        var result = await adapter.ExecuteWebFetchAsync(
            new ResponsesWebFetchBoundaryInput("https://example.com/docs", string.Empty),
            CancellationToken.None);

        result.Url.Should().Be("https://example.com/docs");
        result.StatusCode.Should().Be(200);
        result.ContentType.Should().Be("text/plain");
        result.Content.Should().Be("fresh body");
        result.RedirectUrl.Should().Be("https://example.com/final");
        webClient.FetchCalls.Should().ContainSingle();
        webClient.FetchCalls[0].Token.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteWebSearchAsync_ShouldForwardTokenAndMapSearchResult()
    {
        var webClient = new RecordingWebApiClient
        {
            SearchResult = new WebSearchResult(
            [
                new WebSearchResultItem(
                    "fresh",
                    "https://example.com/fresh",
                    "fresh snippet"),
            ]),
        };
        var adapter = new ResponsesWebSubstituteBackendAdapter(
            webClient,
            new WebToolOptions { MaxSearchResults = 7 });

        var result = await adapter.ExecuteWebSearchAsync(
            new ResponsesWebSearchBoundaryInput("aevatar docs", 5, "secret-token"),
            CancellationToken.None);

        result.Result.ResultCase.Should().Be(ResponsesWebToolResult.ResultOneofCase.Search);
        result.Result.Search.Results.Should().ContainSingle();
        result.Result.Search.Results[0].Title.Should().Be("fresh");
        result.Result.Search.Results[0].Url.Should().Be("https://example.com/fresh");
        result.Result.Search.Results[0].Snippet.Should().Be("fresh snippet");
        webClient.SearchCalls.Should().ContainSingle();
        webClient.SearchCalls[0].Token.Should().Be("secret-token");
        webClient.SearchCalls[0].Query.Should().Be("aevatar docs");
        webClient.SearchCalls[0].MaxResults.Should().Be(5);
    }

    [Fact]
    public async Task ExecuteWebSearchAsync_WhenProviderReturnsNoResults_ShouldReturnEmptyTypedResults()
    {
        var webClient = new RecordingWebApiClient
        {
            SearchResult = WebSearchResult.Empty,
        };
        var adapter = new ResponsesWebSubstituteBackendAdapter(
            webClient,
            new WebToolOptions { MaxSearchResults = 7 });

        var result = await adapter.ExecuteWebSearchAsync(
            new ResponsesWebSearchBoundaryInput("aevatar docs", 5, "secret-token"),
            CancellationToken.None);

        result.Result.ResultCase.Should().Be(ResponsesWebToolResult.ResultOneofCase.Search);
        result.Result.Search.Results.Should().BeEmpty();
        webClient.SearchCalls.Should().ContainSingle();
        webClient.SearchCalls[0].Token.Should().Be("secret-token");
        webClient.SearchCalls[0].Query.Should().Be("aevatar docs");
        webClient.SearchCalls[0].MaxResults.Should().Be(5);
    }

    [Fact]
    public async Task ExecuteWebSearchAsync_WhenProviderReturnsTypedError_ShouldPreserveErrorBranch()
    {
        var webClient = new RecordingWebApiClient
        {
            SearchResult = new WebSearchResult(
                Array.Empty<WebSearchResultItem>(),
                new WebToolError(
                    "search_backend_not_configured",
                    "No search backend configured.")),
        };
        var adapter = new ResponsesWebSubstituteBackendAdapter(
            webClient,
            new WebToolOptions { MaxSearchResults = 7 });

        var result = await adapter.ExecuteWebSearchAsync(
            new ResponsesWebSearchBoundaryInput("official X API documentation", 5, "secret-token"),
            CancellationToken.None);

        result.Result.ResultCase.Should().Be(ResponsesWebToolResult.ResultOneofCase.Error);
        result.Result.Error.Code.Should().Be("search_backend_not_configured");
        result.Result.Error.Message.Should().Be("No search backend configured.");
    }

    [Fact]
    public void HostComposition_ShouldBindResponsesWebBackendPortToAdapter()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWebApiClient, RecordingWebApiClient>();
        services.AddSingleton(new WebToolOptions { MaxSearchResults = 11 });
        services.AddSingleton<IResponsesWebSubstituteBackend, ResponsesWebSubstituteBackendAdapter>();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IResponsesWebSubstituteBackend>()
            .Should()
            .BeOfType<ResponsesWebSubstituteBackendAdapter>();
    }

    private sealed class RecordingWebApiClient : IWebApiClient
    {
        public List<(string Token, string Query, int MaxResults)> SearchCalls { get; } = [];

        public List<(string Token, string Url)> FetchCalls { get; } = [];

        public WebSearchResult SearchResult { get; init; } = WebSearchResult.Empty;

        public WebFetchResult FetchResult { get; init; } = new(
            200,
            "text/plain",
            "body",
            null,
            "https://example.com");

        public Task<WebSearchResult> SearchAsync(string token, string query, int maxResults, CancellationToken ct)
        {
            SearchCalls.Add((token, query, maxResults));
            return Task.FromResult(SearchResult);
        }

        public Task<WebFetchResult> FetchUrlAsync(string token, string url, CancellationToken ct)
        {
            FetchCalls.Add((token, url));
            return Task.FromResult(FetchResult);
        }
    }

}
