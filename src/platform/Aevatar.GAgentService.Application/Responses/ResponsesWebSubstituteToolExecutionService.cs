using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Sockets;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Responses;

namespace Aevatar.GAgentService.Application.Responses;

public sealed class ResponsesWebSubstituteToolExecutionService
{
    private readonly IResponsesAgentToolStateCommandPort _commandPort;
    private readonly IResponsesAgentToolStateQueryPort _queryPort;
    private readonly IResponsesWebSubstituteBackend _backend;

    public ResponsesWebSubstituteToolExecutionService(
        IResponsesAgentToolStateCommandPort commandPort,
        IResponsesAgentToolStateQueryPort queryPort,
        IResponsesWebSubstituteBackend backend)
    {
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async Task<ResponsesWebSubstituteToolExecutionResult> ExecuteAsync(
        ResponsesWebSubstituteToolExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Refactor (issue1323-first): canonicalize local Responses Web aliases before cache identity is built.
        var canonicalToolName = CanonicalizeWebToolName(request.ToolName);

        // Refactor (iter159/cluster-624):
        //   Old pattern: Host 层 ResponsesAevatarToolProvider 直接编排 WebFetch/WebSearch 工具调用
        //   New principle: 编排下沉到 Application/AI 边界;Host 只做协议适配
        return canonicalToolName switch
        {
            "WebFetch" => await ExecuteFetchAsync(request, canonicalToolName, ct).ConfigureAwait(false),
            "WebSearch" => await ExecuteSearchAsync(request, canonicalToolName, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported Responses Web substitute tool '{request.ToolName}'."),
        };
    }

    private async Task<ResponsesWebSubstituteToolExecutionResult> ExecuteFetchAsync(
        ResponsesWebSubstituteToolExecutionRequest request,
        string canonicalToolName,
        CancellationToken ct)
    {
        var validation = ResponsesWebFetchUrlGuard.Validate(NormalizeUrl(request.Fetch?.Url));
        if (!validation.IsAllowed)
        {
            return Error(validation.RejectionCode ?? "url_rejected");
        }

        var url = validation.NormalizedUrl!;
        var cacheKey = ComputeCacheKey(canonicalToolName, url);
        // Refactor (iter161-cluster-001 #1251-first):
        //   Old pattern: fetch trace/cache result was downgraded to Value before recording.
        //   New principle: trace/cache normal writes carry typed ResponsesWebToolResult.
        var cached = await _queryPort.GetWebCacheEntryAsync(
            request.ScopeId,
            request.OwnerSubject,
            canonicalToolName,
            cacheKey,
            ct).ConfigureAwait(false);
        if (cached != null)
        {
            await RecordTraceAsync(
                request,
                canonicalToolName,
                cacheKey,
                url,
                query: string.Empty,
                cacheHit: true,
                cached.Result,
                ct).ConfigureAwait(false);
            return new ResponsesWebSubstituteToolExecutionResult
            {
                TypedCached = cached.Result.Clone(),
            };
        }

        var output = await _backend.ExecuteWebFetchAsync(
            new ResponsesWebFetchBoundaryInput(
                url,
                request.Fetch?.ExtractHint ?? string.Empty),
            ct).ConfigureAwait(false);
        var protoOutput = new ResponsesWebFetchToolOutput
        {
            Url = output.Url,
            StatusCode = output.StatusCode,
            ContentType = output.ContentType,
            Content = output.Content,
            RedirectUrl = output.RedirectUrl,
        };
        var typedOutput = ResponsesWebResultMigration.FromFetch(protoOutput);
        await RecordTraceAsync(
            request,
            canonicalToolName,
            cacheKey,
            url,
            query: string.Empty,
            cacheHit: false,
            typedOutput,
            ct).ConfigureAwait(false);
        return new ResponsesWebSubstituteToolExecutionResult { Fetch = protoOutput };
    }

    private async Task<ResponsesWebSubstituteToolExecutionResult> ExecuteSearchAsync(
        ResponsesWebSubstituteToolExecutionRequest request,
        string canonicalToolName,
        CancellationToken ct)
    {
        var query = request.Search?.Query;
        if (string.IsNullOrWhiteSpace(query))
            return Error("'query' is required");

        var maxResults = request.Search?.MaxResults > 0
            ? request.Search.MaxResults
            : _backend.DefaultMaxSearchResults;
        maxResults = Math.Clamp(maxResults, 1, 20);
        var cacheValue = $"{query.Trim()}\n{maxResults}";
        var cacheKey = ComputeCacheKey(canonicalToolName, cacheValue);
        // Refactor (iter161-cluster-001 #1251-first):
        //   Old pattern: search/error trace/cache results were written as Value.
        //   New principle: typed search/error results are written through the existing command->actor->projection chain.
        var cached = await _queryPort.GetWebCacheEntryAsync(
            request.ScopeId,
            request.OwnerSubject,
            canonicalToolName,
            cacheKey,
            ct).ConfigureAwait(false);
        if (cached != null)
        {
            await RecordTraceAsync(
                request,
                canonicalToolName,
                cacheKey,
                query,
                cacheHit: true,
                cached.Result,
                ct).ConfigureAwait(false);
            return new ResponsesWebSubstituteToolExecutionResult
            {
                TypedCached = cached.Result.Clone(),
            };
        }

        if (string.IsNullOrWhiteSpace(request.NyxIdAccessToken))
        {
            var authError = ResponsesWebResultMigration.FromError(
                "auth_required",
                "No NyxID access token available. User must be authenticated.");
            await RecordTraceAsync(
                request,
                canonicalToolName,
                cacheKey,
                query,
                cacheHit: false,
                authError,
                ct).ConfigureAwait(false);
            return new ResponsesWebSubstituteToolExecutionResult { TypedError = authError.Error.Clone() };
        }

        var searchResult = (await _backend.ExecuteWebSearchAsync(
                new ResponsesWebSearchBoundaryInput(
                    query.Trim(),
                    maxResults,
                    request.NyxIdAccessToken),
                ct).ConfigureAwait(false)).Result;
        var typedSearchResult = searchResult.ResultCase is
            ResponsesWebToolResult.ResultOneofCase.Search or
            ResponsesWebToolResult.ResultOneofCase.Error
                ? searchResult.Clone()
                : ResponsesWebResultMigration.FromError(
                    "search_backend_invalid_result",
                    "The search backend returned no typed result.");
        await RecordTraceAsync(
            request,
            canonicalToolName,
            cacheKey,
            query,
            cacheHit: false,
            typedSearchResult,
            ct).ConfigureAwait(false);
        return typedSearchResult.ResultCase == ResponsesWebToolResult.ResultOneofCase.Error
            ? new ResponsesWebSubstituteToolExecutionResult
            {
                TypedError = typedSearchResult.Error.Clone(),
            }
            : new ResponsesWebSubstituteToolExecutionResult
            {
                TypedSearch = typedSearchResult.Search.Clone(),
            };
    }

    private Task RecordTraceAsync(
        ResponsesWebSubstituteToolExecutionRequest request,
        string canonicalToolName,
        string cacheKey,
        string url,
        string query,
        bool cacheHit,
        ResponsesWebToolResult result,
        CancellationToken ct) =>
        _commandPort.RecordWebTraceAsync(
            request.ScopeId,
            request.OwnerSubject,
            request.ResponseId,
            new ResponsesWebTraceInput(
                ResponseAgentToolStateIds.NewWebTraceId(),
                canonicalToolName,
                cacheKey,
                url,
                query,
                cacheHit,
                result.Clone()),
            ct);

    private Task RecordTraceAsync(
        ResponsesWebSubstituteToolExecutionRequest request,
        string canonicalToolName,
        string cacheKey,
        string query,
        bool cacheHit,
        ResponsesWebToolResult result,
        CancellationToken ct) =>
        RecordTraceAsync(request, canonicalToolName, cacheKey, url: string.Empty, query, cacheHit, result, ct);

    public static string ComputeCacheKey(string toolName, string value)
    {
        var canonicalToolName = CanonicalizeWebToolName(toolName);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{canonicalToolName}\n{value.Trim().ToLowerInvariant()}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string CanonicalizeWebToolName(string toolName) =>
        toolName switch
        {
            "web_fetch" => "WebFetch",
            "web_search" => "WebSearch",
            _ => toolName,
        };

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
        return new ResponsesWebSubstituteToolExecutionResult
        {
            TypedError = ResponsesWebResultMigration.FromError(code).Error,
        };
    }

    private static class ResponsesWebFetchUrlGuard
    {
        public static ResponsesWebFetchValidationResult Validate(string? candidate)
        {
            var trimmed = candidate?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return ResponsesWebFetchValidationResult.Reject("empty_url");

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
                return ResponsesWebFetchValidationResult.Reject("invalid_url");

            if (uri.Scheme is not ("http" or "https"))
                return ResponsesWebFetchValidationResult.Reject("unsupported_scheme");

            if (string.IsNullOrEmpty(uri.Host))
                return ResponsesWebFetchValidationResult.Reject("missing_host");

            if (IsHostLiteralIp(uri.Host, out var address) && IsBlockedAddress(address))
                return ResponsesWebFetchValidationResult.Reject("blocked_private_address");

            if (IsLoopbackHostname(uri.Host))
                return ResponsesWebFetchValidationResult.Reject("blocked_loopback_hostname");

            return ResponsesWebFetchValidationResult.Accept(uri.ToString());
        }

        private static bool IsHostLiteralIp(string host, out IPAddress address)
        {
            var stripped = host.StartsWith('[') && host.EndsWith(']')
                ? host[1..^1]
                : host;
            return IPAddress.TryParse(stripped, out address!);
        }

        private static bool IsBlockedAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
                return true;

            if (address.AddressFamily == AddressFamily.InterNetwork)
                return IsBlockedIpv4(address.GetAddressBytes());

            if (address.AddressFamily != AddressFamily.InterNetworkV6)
                return false;

            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal)
                return true;

            return address.IsIPv4MappedToIPv6 && IsBlockedIpv4(address.MapToIPv4().GetAddressBytes());
        }

        private static bool IsBlockedIpv4(byte[] octets)
        {
            if (octets.Length != 4)
                return false;

            return octets[0] == 10
                   || octets[0] == 127
                   || (octets[0] == 169 && octets[1] == 254)
                   || (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
                   || (octets[0] == 192 && octets[1] == 168)
                   || (octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127)
                   || (octets[0] == 192 && octets[1] == 0 && octets[2] == 0)
                   || (octets[0] == 198 && (octets[1] == 18 || octets[1] == 19))
                   || octets[0] == 0
                   || octets[0] >= 224;
        }

        private static bool IsLoopbackHostname(string host) =>
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "ip6-localhost", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct ResponsesWebFetchValidationResult(
        bool IsAllowed,
        string? NormalizedUrl,
        string? RejectionCode)
    {
        public static ResponsesWebFetchValidationResult Accept(string normalizedUrl) =>
            new(true, normalizedUrl, null);

        public static ResponsesWebFetchValidationResult Reject(string code) =>
            new(false, null, code);
    }
}
