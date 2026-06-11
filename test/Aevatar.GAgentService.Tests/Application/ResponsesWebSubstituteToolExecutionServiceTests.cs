using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
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
        var backend = new RecordingResponsesWebSubstituteBackend();
        var service = CreateService(state, backend);

        var result = await service.ExecuteAsync(CreateRequest(
            "WebFetch",
            """{"url":"http://example.com/docs"}"""));

        result.TypedCached.Fetch.Content.Should().Be("cached");
        backend.FetchCalls.Should().BeEmpty();
        state.WebTraces.Should().ContainSingle();
        state.WebTraces[0].Trace.CacheKey.Should().Be(cacheKey);
        state.WebTraces[0].Trace.Url.Should().Be("https://example.com/docs");
        state.WebTraces[0].Trace.CacheHit.Should().BeTrue();
        state.WebTraces[0].Trace.Result.Fetch.Content.Should().Be("cached");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCanonicalizeFetchAliasBeforeCacheIdentity()
    {
        var state = new RecordingResponsesAgentToolStatePort();
        var cacheKey = state.SeedWebCache(
            "WebFetch",
            "https://example.com/docs",
            """{"url":"https://example.com/docs","content":"cached"}""");
        var backend = new RecordingResponsesWebSubstituteBackend();
        var service = CreateService(state, backend);

        var result = await service.ExecuteAsync(CreateRequest(
            "web_fetch",
            """{"url":"http://example.com/docs"}"""));

        result.TypedCached.Fetch.Content.Should().Be("cached");
        backend.FetchCalls.Should().BeEmpty();
        state.WebCacheLookups.Should().ContainSingle(x =>
            x.ToolName == "WebFetch" &&
            x.CacheKey == cacheKey);
        state.WebTraces.Should().ContainSingle();
        state.WebTraces[0].Trace.ToolName.Should().Be("WebFetch");
        state.WebTraces[0].Trace.CacheKey.Should().Be(cacheKey);
        ResponsesWebSubstituteToolExecutionService
            .ComputeCacheKey("web_fetch", "https://example.com/docs")
            .Should()
            .Be(cacheKey);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFetchWithoutForwardingNyxIdTokenAndRecordTrace()
    {
        var state = new RecordingResponsesAgentToolStatePort();
        var backend = new RecordingResponsesWebSubstituteBackend
        {
            FetchResult = new ResponsesWebFetchBoundaryResult(
                "https://example.com/docs",
                200,
                "text/plain",
                "fresh body",
                string.Empty),
        };
        var service = CreateService(state, backend);

        var result = await service.ExecuteAsync(CreateRequest(
            "WebFetch",
            """{"url":"https://example.com/docs"}""",
            token: "secret-token"));

        result.Fetch.Content.Should().Contain("fresh body");
        backend.FetchCalls.Should().ContainSingle();
        backend.FetchCalls[0].Url.Should().Be("https://example.com/docs");
        state.WebTraces.Should().ContainSingle();
        state.WebTraces[0].Trace.CacheHit.Should().BeFalse();
        state.WebTraces[0].Trace.Result.Fetch.Content.Should().Be("fresh body");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectInvalidFetchUrlBeforeCallingWebClient()
    {
        var state = new RecordingResponsesAgentToolStatePort();
        var backend = new RecordingResponsesWebSubstituteBackend();
        var service = CreateService(state, backend);

        var result = await service.ExecuteAsync(CreateRequest(
            "WebFetch",
            """{"url":"http://127.0.0.1/admin"}"""));

        result.TypedError.Code.Should().Be("blocked_private_address");
        backend.FetchCalls.Should().BeEmpty();
        state.WebTraces.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSearchWithClampedMaxResultsAndRecordTrace()
    {
        var state = new RecordingResponsesAgentToolStatePort();
        var backend = new RecordingResponsesWebSubstituteBackend
        {
            SearchResult = new ResponsesWebSearchBoundaryResult(
                SearchOutput(("fresh", "https://example.com/fresh", "snippet"))),
        };
        var service = CreateService(state, backend);

        var result = await service.ExecuteAsync(CreateRequest(
            "WebSearch",
            """{"query":"aevatar docs","max_results":99}""",
            token: "secret-token"));

        result.TypedSearch.Results[0].Title.Should().Be("fresh");
        backend.SearchCalls.Should().ContainSingle();
        backend.SearchCalls[0].NyxIdAccessToken.Should().Be("secret-token");
        backend.SearchCalls[0].Query.Should().Be("aevatar docs");
        backend.SearchCalls[0].MaxResults.Should().Be(20);
        state.WebTraces.Should().ContainSingle();
        state.WebTraces[0].Trace.Query.Should().Be("aevatar docs");
        state.WebTraces[0].Trace.CacheHit.Should().BeFalse();
        state.WebTraces[0].Trace.Result.Search.Results[0].Title.Should().Be("fresh");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCanonicalizeSearchAliasBeforeCacheIdentity()
    {
        var state = new RecordingResponsesAgentToolStatePort();
        var cacheKey = state.SeedWebCache(
            "WebSearch",
            "aevatar docs\n3",
            """{"results":[{"title":"cached","url":"https://example.com/cached","snippet":"snippet"}]}""");
        var backend = new RecordingResponsesWebSubstituteBackend();
        var service = CreateService(state, backend);

        var result = await service.ExecuteAsync(CreateRequest(
            "web_search",
            """{"query":" aevatar docs ","max_results":3}""",
            token: "secret-token"));

        result.TypedCached.Search.Results.Should().ContainSingle(x => x.Title == "cached");
        backend.SearchCalls.Should().BeEmpty();
        state.WebCacheLookups.Should().ContainSingle(x =>
            x.ToolName == "WebSearch" &&
            x.CacheKey == cacheKey);
        state.WebTraces.Should().ContainSingle();
        state.WebTraces[0].Trace.ToolName.Should().Be("WebSearch");
        state.WebTraces[0].Trace.CacheKey.Should().Be(cacheKey);
        ResponsesWebSubstituteToolExecutionService
            .ComputeCacheKey("web_search", "aevatar docs\n3")
            .Should()
            .Be(cacheKey);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnSearchAuthErrorAndRecordTraceWhenTokenMissing()
    {
        var state = new RecordingResponsesAgentToolStatePort();
        var backend = new RecordingResponsesWebSubstituteBackend();
        var service = CreateService(state, backend);

        var result = await service.ExecuteAsync(CreateRequest(
            "WebSearch",
            """{"query":"aevatar docs"}""",
            token: string.Empty));

        result.TypedError.Code.Should().Be("auth_required");
        result.TypedError.Message.Should().Be("No NyxID access token available. User must be authenticated.");
        backend.SearchCalls.Should().BeEmpty();
        state.WebTraces.Should().ContainSingle();
        state.WebTraces[0].Trace.CacheHit.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDependOnlyOnResponsesOwnedBackendBoundary()
    {
        var state = new RecordingResponsesAgentToolStatePort();
        var backend = new RecordingResponsesWebSubstituteBackend();
        var service = CreateService(state, backend);

        await service.ExecuteAsync(CreateRequest(
            "WebSearch",
            """{"query":"aevatar docs"}"""));

        backend.SearchCalls.Should().ContainSingle();
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

    [Fact]
    public void ApplicationProject_ShouldNotReferenceConcreteWebToolProvider()
    {
        var projectPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../src/platform/Aevatar.GAgentService.Application/Aevatar.GAgentService.Application.csproj"));

        File.ReadAllText(projectPath)
            .Should()
            .NotContain("Aevatar.AI.ToolProviders.Web");
    }

    private static ResponsesWebSubstituteToolExecutionService CreateService(
        RecordingResponsesAgentToolStatePort state,
        RecordingResponsesWebSubstituteBackend backend) =>
        new(state, state, backend);

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

    private sealed class RecordingResponsesWebSubstituteBackend : IResponsesWebSubstituteBackend
    {
        public List<ResponsesWebSearchBoundaryInput> SearchCalls { get; } = [];

        public List<ResponsesWebFetchBoundaryInput> FetchCalls { get; } = [];

        public ResponsesWebSearchBoundaryResult SearchResult { get; init; } =
            new(new ResponsesWebSearchToolOutput());

        public ResponsesWebFetchBoundaryResult FetchResult { get; init; } = new(
            "https://example.com",
            200,
            "text/plain",
            "body",
            string.Empty);

        public int DefaultMaxSearchResults { get; init; } = 3;

        public Task<ResponsesWebSearchBoundaryResult> ExecuteWebSearchAsync(
            ResponsesWebSearchBoundaryInput input,
            CancellationToken ct)
        {
            SearchCalls.Add(input);
            return Task.FromResult(new ResponsesWebSearchBoundaryResult(SearchResult.Output.Clone()));
        }

        public Task<ResponsesWebFetchBoundaryResult> ExecuteWebFetchAsync(
            ResponsesWebFetchBoundaryInput input,
            CancellationToken ct)
        {
            FetchCalls.Add(input);
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

        public List<(string ScopeId, string OwnerSubject, string ToolName, string CacheKey)> WebCacheLookups { get; } = [];

        public string SeedWebCache(string toolName, string value, string resultJson)
        {
            var cacheKey = ResponsesWebSubstituteToolExecutionService.ComputeCacheKey(toolName, value);
            _webCache[(toolName, cacheKey)] = new ResponsesWebCacheEntrySnapshot(
                cacheKey,
                toolName,
                value,
                string.Empty,
                ResponsesWebResultMigration.FromLegacyValue(JsonParser.Default.Parse<ProtoValue>(resultJson)),
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
            WebCacheLookups.Add((scopeId, ownerSubject, toolName, cacheKey));
            _webCache.TryGetValue((toolName, cacheKey), out var entry);
            return Task.FromResult(entry);
        }
    }

    private static ResponsesWebSearchToolOutput SearchOutput(params (string Title, string Url, string Snippet)[] items)
    {
        var output = new ResponsesWebSearchToolOutput();
        output.Results.AddRange(items.Select(static item => new ResponsesWebSearchResultItem
        {
            Title = item.Title,
            Url = item.Url,
            Snippet = item.Snippet,
        }));
        return output;
    }
}
