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

        result.Output.Results.Should().ContainSingle();
        result.Output.Results[0].Title.Should().Be("fresh");
        result.Output.Results[0].Url.Should().Be("https://example.com/fresh");
        result.Output.Results[0].Snippet.Should().Be("fresh snippet");
        webClient.SearchCalls.Should().ContainSingle();
        webClient.SearchCalls[0].Token.Should().Be("secret-token");
        webClient.SearchCalls[0].Query.Should().Be("aevatar docs");
        webClient.SearchCalls[0].MaxResults.Should().Be(5);
    }

    [Theory]
    [MemberData(nameof(MalformedSearchResults))]
    public async Task ExecuteWebSearchAsync_WhenProviderTypedResultHasNoResults_ShouldReturnEmptyTypedResults(
        WebSearchResult providerValue)
    {
        var webClient = new RecordingWebApiClient
        {
            SearchResult = providerValue,
        };
        var adapter = new ResponsesWebSubstituteBackendAdapter(
            webClient,
            new WebToolOptions { MaxSearchResults = 7 });

        var result = await adapter.ExecuteWebSearchAsync(
            new ResponsesWebSearchBoundaryInput("aevatar docs", 5, "secret-token"),
            CancellationToken.None);

        result.Output.Results.Should().BeEmpty();
        webClient.SearchCalls.Should().ContainSingle();
        webClient.SearchCalls[0].Token.Should().Be("secret-token");
        webClient.SearchCalls[0].Query.Should().Be("aevatar docs");
        webClient.SearchCalls[0].MaxResults.Should().Be(5);
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

    public static TheoryData<WebSearchResult> MalformedSearchResults() =>
        new()
        {
            WebSearchResult.Empty,
            new WebSearchResult(
                Array.Empty<WebSearchResultItem>(),
                new WebToolError("unstructured_search_result", "bad")),
        };
}
