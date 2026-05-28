using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.ToolProviders.Web;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Application.Responses;

public sealed class ResponsesWebSubstituteToolExecutionService
{
    private readonly IResponsesAgentToolStateCommandPort _commandPort;
    private readonly IResponsesAgentToolStateQueryPort _queryPort;
    private readonly IWebApiClient _webClient;
    private readonly WebToolOptions _webOptions;

    public ResponsesWebSubstituteToolExecutionService(
        IResponsesAgentToolStateCommandPort commandPort,
        IResponsesAgentToolStateQueryPort queryPort,
        IWebApiClient webClient,
        WebToolOptions webOptions)
    {
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _webClient = webClient ?? throw new ArgumentNullException(nameof(webClient));
        _webOptions = webOptions ?? throw new ArgumentNullException(nameof(webOptions));
    }

    public async Task<ResponsesWebSubstituteToolExecutionResult> ExecuteAsync(
        ResponsesWebSubstituteToolExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Refactor (iter159/cluster-624):
        //   Old pattern: Host 层 ResponsesAevatarToolProvider 直接编排 WebFetch/WebSearch 工具调用
        //   New principle: 编排下沉到 Application/AI 边界;Host 只做协议适配
        return request.ToolName switch
        {
            "WebFetch" or "web_fetch" => await ExecuteFetchAsync(request, ct).ConfigureAwait(false),
            "WebSearch" or "web_search" => await ExecuteSearchAsync(request, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported Responses Web substitute tool '{request.ToolName}'."),
        };
    }

    private async Task<ResponsesWebSubstituteToolExecutionResult> ExecuteFetchAsync(
        ResponsesWebSubstituteToolExecutionRequest request,
        CancellationToken ct)
    {
        var validation = WebFetchUrlGuard.Validate(NormalizeUrl(request.Fetch?.Url));
        if (!validation.IsAllowed)
        {
            return Error(validation.RejectionCode ?? "url_rejected");
        }

        var url = validation.NormalizedUrl!;
        var cacheKey = ComputeCacheKey(request.ToolName, url);
        var cached = await _queryPort.GetWebCacheEntryAsync(
            request.ScopeId,
            request.OwnerSubject,
            request.ToolName,
            cacheKey,
            ct).ConfigureAwait(false);
        if (cached != null)
        {
            await RecordTraceAsync(
                request,
                cacheKey,
                url,
                query: string.Empty,
                cacheHit: true,
                cached.Result,
                ct).ConfigureAwait(false);
            return new ResponsesWebSubstituteToolExecutionResult
            {
                Cached = cached.Result.Clone(),
            };
        }

        // The URL came from LLM-controlled input. Never forward the caller's
        // NyxID bearer to an arbitrary fetch target.
        var result = await _webClient.FetchUrlAsync(token: string.Empty, url, ct).ConfigureAwait(false);
        var output = new ResponsesWebFetchToolOutput
        {
            Url = result.OriginalUrl,
            StatusCode = result.StatusCode,
            ContentType = result.ContentType,
            Content = result.Body ?? string.Empty,
            RedirectUrl = result.RedirectUrl ?? string.Empty,
        };
        var outputValue = ToValue(output);
        await RecordTraceAsync(
            request,
            cacheKey,
            url,
            query: string.Empty,
            cacheHit: false,
            outputValue,
            ct).ConfigureAwait(false);
        return new ResponsesWebSubstituteToolExecutionResult { Fetch = output };
    }

    private async Task<ResponsesWebSubstituteToolExecutionResult> ExecuteSearchAsync(
        ResponsesWebSubstituteToolExecutionRequest request,
        CancellationToken ct)
    {
        var query = request.Search?.Query;
        if (string.IsNullOrWhiteSpace(query))
            return Error("'query' is required");

        var maxResults = request.Search?.MaxResults > 0 ? request.Search.MaxResults : _webOptions.MaxSearchResults;
        maxResults = Math.Clamp(maxResults, 1, 20);
        var cacheValue = $"{query.Trim()}\n{maxResults}";
        var cacheKey = ComputeCacheKey(request.ToolName, cacheValue);
        var cached = await _queryPort.GetWebCacheEntryAsync(
            request.ScopeId,
            request.OwnerSubject,
            request.ToolName,
            cacheKey,
            ct).ConfigureAwait(false);
        if (cached != null)
        {
            await RecordTraceAsync(
                request,
                cacheKey,
                query,
                cacheHit: true,
                cached.Result,
                ct).ConfigureAwait(false);
            return new ResponsesWebSubstituteToolExecutionResult
            {
                Cached = cached.Result.Clone(),
            };
        }

        var searchResult = string.IsNullOrWhiteSpace(request.NyxIdAccessToken)
            ? ErrorValue("No NyxID access token available. User must be authenticated.")
            : await _webClient.SearchAsync(request.NyxIdAccessToken, query.Trim(), maxResults, ct).ConfigureAwait(false);
        await RecordTraceAsync(
            request,
            cacheKey,
            query,
            cacheHit: false,
            searchResult,
            ct).ConfigureAwait(false);
        return new ResponsesWebSubstituteToolExecutionResult { Search = searchResult };
    }

    private Task RecordTraceAsync(
        ResponsesWebSubstituteToolExecutionRequest request,
        string cacheKey,
        string url,
        string query,
        bool cacheHit,
        Value result,
        CancellationToken ct) =>
        _commandPort.RecordWebTraceAsync(
            request.ScopeId,
            request.OwnerSubject,
            request.ResponseId,
            new ResponsesWebTraceInput(
                ResponseAgentToolStateIds.NewWebTraceId(),
                request.ToolName,
                cacheKey,
                url,
                query,
                cacheHit,
                result.Clone()),
            ct);

    private Task RecordTraceAsync(
        ResponsesWebSubstituteToolExecutionRequest request,
        string cacheKey,
        string query,
        bool cacheHit,
        Value result,
        CancellationToken ct) =>
        RecordTraceAsync(request, cacheKey, url: string.Empty, query, cacheHit, result, ct);

    public static string ComputeCacheKey(string toolName, string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{toolName}\n{value.Trim().ToLowerInvariant()}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NormalizeUrl(string? url)
    {
        var normalized = url?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;
        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "https://" + normalized[7..];
        return normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : "https://" + normalized;
    }

    private static ResponsesWebSubstituteToolExecutionResult Error(string code)
    {
        return new ResponsesWebSubstituteToolExecutionResult { Error = ErrorValue(code) };
    }

    private static Value ErrorValue(string code)
    {
        var error = new Value { StructValue = new Struct() };
        error.StructValue.Fields["error"] = Value.ForString(code);
        return error;
    }

    private static Value ToValue(ResponsesWebFetchToolOutput output)
    {
        var value = new Value { StructValue = new Struct() };
        value.StructValue.Fields["url"] = Value.ForString(output.Url);
        value.StructValue.Fields["status_code"] = Value.ForNumber(output.StatusCode);
        value.StructValue.Fields["content_type"] = Value.ForString(output.ContentType);
        value.StructValue.Fields["content"] = Value.ForString(output.Content);
        if (!string.IsNullOrWhiteSpace(output.RedirectUrl))
            value.StructValue.Fields["redirect_url"] = Value.ForString(output.RedirectUrl);
        return value;
    }
}
