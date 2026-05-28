using Aevatar.AI.ToolProviders.Web;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;
using System.Text.Json;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ResponsesWebSubstituteToolExecutionServiceTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldUseCachedFetchReadModelAndRecordTrace()
    {
        var state = new RecordingResponsesAgentToolStatePort();
        var cacheKey = state.SeedWebCache(
            "WebFetch",
            "https://example.com/docs",
            """{"url":"https://example.com/docs","content":"cached"}""");
        var webClient = new RecordingWebApiClient();
        var service = CreateService(state, webClient);

        var result = await service.ExecuteAsync(CreateRequest(
            "WebFetch",
            """{"url":"http://example.com/docs"}"""));

        result.ResultJson.Should().Contain("cached");
        webClient.FetchCalls.Should().BeEmpty();
        state.WebTraces.Should().ContainSingle();
        state.WebTraces[0].Trace.CacheKey.Should().Be(cacheKey);
        state.WebTraces[0].Trace.Url.Should().Be("https://example.com/docs");
        state.WebTraces[0].Trace.CacheHit.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFetchWithoutForwardingNyxIdTokenAndRecordTrace()
    {
        var state = new RecordingResponsesAgentToolStatePort();
        var webClient = new RecordingWebApiClient
        {
            FetchResult = new FetchResult(
                200,
                "text/plain",
                "fresh body",
                null,
                "https://example.com/docs"),
        };
        var service = CreateService(state, webClient);

        var result = await service.ExecuteAsync(CreateRequest(
            "WebFetch",
            """{"url":"https://example.com/docs"}""",
            token: "secret-token"));

        result.ResultJson.Should().Contain("fresh body");
        webClient.FetchCalls.Should().ContainSingle();
        webClient.FetchCalls[0].Token.Should().BeEmpty();
        state.WebTraces.Should().ContainSingle();
        state.WebTraces[0].Trace.CacheHit.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectInvalidFetchUrlBeforeCallingWebClient()
    {
        var state = new RecordingResponsesAgentToolStatePort();
        var webClient = new RecordingWebApiClient();
        var service = CreateService(state, webClient);

        var result = await service.ExecuteAsync(CreateRequest(
            "WebFetch",
            """{"url":"http://127.0.0.1/admin"}"""));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be("blocked_private_address");
        webClient.FetchCalls.Should().BeEmpty();
        state.WebTraces.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSearchWithClampedMaxResultsAndRecordTrace()
    {
        var state = new RecordingResponsesAgentToolStatePort();
        var webClient = new RecordingWebApiClient
        {
            SearchResultJson = """{"results":[{"title":"fresh"}]}""",
        };
        var service = CreateService(state, webClient);

        var result = await service.ExecuteAsync(CreateRequest(
            "WebSearch",
            """{"query":"aevatar docs","max_results":99}""",
            token: "secret-token"));

        result.ResultJson.Should().Contain("fresh");
        webClient.SearchCalls.Should().ContainSingle();
        webClient.SearchCalls[0].Token.Should().Be("secret-token");
        webClient.SearchCalls[0].Query.Should().Be("aevatar docs");
        webClient.SearchCalls[0].MaxResults.Should().Be(20);
        state.WebTraces.Should().ContainSingle();
        state.WebTraces[0].Trace.Query.Should().Be("aevatar docs");
        state.WebTraces[0].Trace.CacheHit.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnSearchAuthErrorAndRecordTraceWhenTokenMissing()
    {
        var state = new RecordingResponsesAgentToolStatePort();
        var webClient = new RecordingWebApiClient();
        var service = CreateService(state, webClient);

        var result = await service.ExecuteAsync(CreateRequest(
            "WebSearch",
            """{"query":"aevatar docs"}""",
            token: string.Empty));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("error").GetString()
            .Should()
            .Be("No NyxID access token available. User must be authenticated.");
        webClient.SearchCalls.Should().BeEmpty();
        state.WebTraces.Should().ContainSingle();
        state.WebTraces[0].Trace.CacheHit.Should().BeFalse();
    }

    private static ResponsesWebSubstituteToolExecutionService CreateService(
        RecordingResponsesAgentToolStatePort state,
        RecordingWebApiClient webClient) =>
        new(state, state, webClient, new WebToolOptions { MaxSearchResults = 3 });

    private static ResponsesWebSubstituteToolExecutionRequest CreateRequest(
        string toolName,
        string argumentsJson,
        string token = "secret-token") =>
        new(toolName, "scope-1", "owner-1", "resp_1", argumentsJson, token);

    private sealed class RecordingWebApiClient : IWebApiClient
    {
        public List<(string Token, string Query, int MaxResults)> SearchCalls { get; } = [];

        public List<(string Token, string Url)> FetchCalls { get; } = [];

        public string SearchResultJson { get; init; } = """{"results":[]}""";

        public FetchResult FetchResult { get; init; } = new(
            200,
            "text/plain",
            "body",
            null,
            "https://example.com");

        public Task<string> SearchAsync(string token, string query, int maxResults, CancellationToken ct)
        {
            SearchCalls.Add((token, query, maxResults));
            return Task.FromResult(SearchResultJson);
        }

        public Task<FetchResult> FetchUrlAsync(string token, string url, CancellationToken ct)
        {
            FetchCalls.Add((token, url));
            return Task.FromResult(FetchResult);
        }
    }

    private sealed class RecordingResponsesAgentToolStatePort :
        IResponsesAgentToolStateCommandPort,
        IResponsesAgentToolStateQueryPort
    {
        private readonly Dictionary<(string ToolName, string CacheKey), ResponsesWebCacheEntrySnapshot> _webCache =
            new();

        public List<(string ScopeId, string OwnerSubject, string SourceResponseId, ResponsesWebTraceInput Trace)> WebTraces { get; } = [];

        public string SeedWebCache(string toolName, string value, string resultJson)
        {
            var cacheKey = ResponsesWebSubstituteToolExecutionService.ComputeCacheKey(toolName, value);
            _webCache[(toolName, cacheKey)] = new ResponsesWebCacheEntrySnapshot(
                cacheKey,
                toolName,
                value,
                string.Empty,
                resultJson,
                DateTimeOffset.UtcNow,
                null,
                0);
            return cacheKey;
        }

        public Task<ResponsesTodoWriteResult> ApplyTodoWriteAsync(
            string scopeId,
            string ownerSubject,
            string sourceResponseId,
            string argumentsJson,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ResponsesTaskDispatchResult> RecordTaskAsync(
            string scopeId,
            string ownerSubject,
            string sourceResponseId,
            string argumentsJson,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ResponsesWebTraceResult> RecordWebTraceAsync(
            string scopeId,
            string ownerSubject,
            string sourceResponseId,
            ResponsesWebTraceInput trace,
            CancellationToken ct = default)
        {
            WebTraces.Add((scopeId, ownerSubject, sourceResponseId, trace));
            return Task.FromResult(new ResponsesWebTraceResult(
                "actor-1",
                trace.TraceId,
                trace.CacheKey,
                trace.CacheHit,
                trace.ResultJson));
        }

        public Task<ResponsesAgentToolStateSnapshot?> GetAsync(
            string scopeId,
            string ownerSubject,
            CancellationToken ct = default) =>
            Task.FromResult<ResponsesAgentToolStateSnapshot?>(null);

        public Task<ResponsesWebCacheEntrySnapshot?> GetWebCacheEntryAsync(
            string scopeId,
            string ownerSubject,
            string toolName,
            string cacheKey,
            CancellationToken ct = default)
        {
            _webCache.TryGetValue((toolName, cacheKey), out var entry);
            return Task.FromResult(entry);
        }
    }
}
