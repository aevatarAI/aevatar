using Aevatar.AI.ToolProviders.Web;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Reflection;
using ProtoValue = Google.Protobuf.WellKnownTypes.Value;

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

        result.Cached.StructValue.Fields["content"].StringValue.Should().Be("cached");
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

        result.Fetch.Content.Should().Contain("fresh body");
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

        result.Error.StructValue.Fields["error"].StringValue.Should().Be("blocked_private_address");
        webClient.FetchCalls.Should().BeEmpty();
        state.WebTraces.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSearchWithClampedMaxResultsAndRecordTrace()
    {
        var state = new RecordingResponsesAgentToolStatePort();
        var webClient = new RecordingWebApiClient
        {
            SearchResult = StructValue(("results", ListValueValue(StructValue(("title", ProtoValue.ForString("fresh")))))),
        };
        var service = CreateService(state, webClient);

        var result = await service.ExecuteAsync(CreateRequest(
            "WebSearch",
            """{"query":"aevatar docs","max_results":99}""",
            token: "secret-token"));

        result.Search.StructValue.Fields["results"].ListValue.Values[0].StructValue.Fields["title"].StringValue
            .Should()
            .Be("fresh");
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

        result.Search.StructValue.Fields["error"].StringValue
            .Should()
            .Be("No NyxID access token available. User must be authenticated.");
        webClient.SearchCalls.Should().BeEmpty();
        state.WebTraces.Should().ContainSingle();
        state.WebTraces[0].Trace.CacheHit.Should().BeFalse();
    }

    [Fact]
    public void ApplicationWebSubstituteContracts_ShouldNotExposeBoundaryJsonStrings()
    {
        typeof(ResponsesWebSubstituteToolExecutionRequest)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(static property => property.Name)
            .Should()
            .NotContain(["ArgumentsJson"]);

        typeof(ResponsesWebSubstituteToolExecutionResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(static property => property.Name)
            .Should()
            .NotContain(["ResultJson"]);
    }

    private static ResponsesWebSubstituteToolExecutionService CreateService(
        RecordingResponsesAgentToolStatePort state,
        RecordingWebApiClient webClient) =>
        new(state, state, webClient, new WebToolOptions { MaxSearchResults = 3 });

    private static ResponsesWebSubstituteToolExecutionRequest CreateRequest(
        string toolName,
        string argumentsJson,
        string token = "secret-token")
    {
        return toolName is "WebFetch" or "web_fetch"
            ? new ResponsesWebSubstituteToolExecutionRequest
            {
                ToolName = toolName,
                ScopeId = "scope-1",
                OwnerSubject = "owner-1",
                ResponseId = "resp_1",
                NyxIdAccessToken = token,
                Fetch = ParseFetch(argumentsJson),
            }
            : new ResponsesWebSubstituteToolExecutionRequest
            {
                ToolName = toolName,
                ScopeId = "scope-1",
                OwnerSubject = "owner-1",
                ResponseId = "resp_1",
                NyxIdAccessToken = token,
                Search = ParseSearch(argumentsJson),
            };
    }

    private static ResponsesWebFetchToolInput ParseFetch(string argumentsJson)
    {
        var value = JsonParser.Default.Parse<ProtoValue>(argumentsJson);
        return new ResponsesWebFetchToolInput
        {
            Url = value.StructValue.Fields.TryGetValue("url", out var url) ? url.StringValue : string.Empty,
        };
    }

    private static ResponsesWebSearchToolInput ParseSearch(string argumentsJson)
    {
        var value = JsonParser.Default.Parse<ProtoValue>(argumentsJson);
        return new ResponsesWebSearchToolInput
        {
            Query = value.StructValue.Fields.TryGetValue("query", out var query) ? query.StringValue : string.Empty,
            MaxResults = value.StructValue.Fields.TryGetValue("max_results", out var maxResults)
                ? (int)maxResults.NumberValue
                : 0,
        };
    }

    private sealed class RecordingWebApiClient : IWebApiClient
    {
        public List<(string Token, string Query, int MaxResults)> SearchCalls { get; } = [];

        public List<(string Token, string Url)> FetchCalls { get; } = [];

        public ProtoValue SearchResult { get; init; } = StructValue(("results", ListValueValue()));

        public FetchResult FetchResult { get; init; } = new(
            200,
            "text/plain",
            "body",
            null,
            "https://example.com");

        public Task<ProtoValue> SearchAsync(string token, string query, int maxResults, CancellationToken ct)
        {
            SearchCalls.Add((token, query, maxResults));
            return Task.FromResult(SearchResult.Clone());
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
                JsonParser.Default.Parse<ProtoValue>(resultJson),
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
                trace.Result.Clone()));
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

    private static ProtoValue StructValue(params (string Key, ProtoValue Value)[] fields)
    {
        var value = new ProtoValue { StructValue = new Struct() };
        foreach (var (key, fieldValue) in fields)
            value.StructValue.Fields[key] = fieldValue;
        return value;
    }

    private static ProtoValue ListValueValue(params ProtoValue[] values)
    {
        var value = new ProtoValue { ListValue = new Google.Protobuf.WellKnownTypes.ListValue() };
        value.ListValue.Values.AddRange(values);
        return value;
    }
}
