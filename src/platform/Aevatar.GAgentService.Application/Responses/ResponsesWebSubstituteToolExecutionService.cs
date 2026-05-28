using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.Web;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;

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
        var validation = WebFetchUrlGuard.Validate(NormalizeUrl(ExtractString(request.ArgumentsJson, "url")));
        if (!validation.IsAllowed)
        {
            return new ResponsesWebSubstituteToolExecutionResult(JsonSerializer.Serialize(new
            {
                error = validation.RejectionCode ?? "url_rejected",
            }));
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
                cached.ResultJson,
                ct).ConfigureAwait(false);
            return new ResponsesWebSubstituteToolExecutionResult(cached.ResultJson);
        }

        // Refactor (iter159/cluster-624-first): Old: Host owned WebFetch/WebSearch orchestration  New: moved to Application/AI traced wrapper.
        // The URL came from LLM-controlled input. Never forward the caller's
        // NyxID bearer to an arbitrary fetch target.
        var result = await _webClient.FetchUrlAsync(token: string.Empty, url, ct).ConfigureAwait(false);
        var resultJson = JsonSerializer.Serialize(new
        {
            url = result.OriginalUrl,
            status_code = result.StatusCode,
            content_type = result.ContentType,
            content = result.Body ?? string.Empty,
            redirect_url = result.RedirectUrl,
        });
        await RecordTraceAsync(
            request,
            cacheKey,
            url,
            query: string.Empty,
            cacheHit: false,
            resultJson,
            ct).ConfigureAwait(false);
        return new ResponsesWebSubstituteToolExecutionResult(resultJson);
    }

    private async Task<ResponsesWebSubstituteToolExecutionResult> ExecuteSearchAsync(
        ResponsesWebSubstituteToolExecutionRequest request,
        CancellationToken ct)
    {
        var query = ExtractString(request.ArgumentsJson, "query");
        if (string.IsNullOrWhiteSpace(query))
            return new ResponsesWebSubstituteToolExecutionResult("""{"error":"'query' is required"}""");

        var maxResults = ExtractMaxResults(request.ArgumentsJson) ?? _webOptions.MaxSearchResults;
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
                cached.ResultJson,
                ct).ConfigureAwait(false);
            return new ResponsesWebSubstituteToolExecutionResult(cached.ResultJson);
        }

        var resultJson = string.IsNullOrWhiteSpace(request.NyxIdAccessToken)
            ? """{"error":"No NyxID access token available. User must be authenticated."}"""
            : await _webClient.SearchAsync(request.NyxIdAccessToken, query.Trim(), maxResults, ct).ConfigureAwait(false);
        await RecordTraceAsync(
            request,
            cacheKey,
            query,
            cacheHit: false,
            resultJson,
            ct).ConfigureAwait(false);
        return new ResponsesWebSubstituteToolExecutionResult(resultJson);
    }

    private Task RecordTraceAsync(
        ResponsesWebSubstituteToolExecutionRequest request,
        string cacheKey,
        string url,
        string query,
        bool cacheHit,
        string resultJson,
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
                resultJson),
            ct);

    private Task RecordTraceAsync(
        ResponsesWebSubstituteToolExecutionRequest request,
        string cacheKey,
        string query,
        bool cacheHit,
        string resultJson,
        CancellationToken ct) =>
        RecordTraceAsync(request, cacheKey, url: string.Empty, query, cacheHit, resultJson, ct);

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

    private static string? ExtractString(string argumentsJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(propertyName, out var value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ExtractMaxResults(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("max_results", out var value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record ResponsesWebSubstituteToolExecutionRequest(
    string ToolName,
    string ScopeId,
    string OwnerSubject,
    string ResponseId,
    string ArgumentsJson,
    string NyxIdAccessToken);

public sealed record ResponsesWebSubstituteToolExecutionResult(string ResultJson);
