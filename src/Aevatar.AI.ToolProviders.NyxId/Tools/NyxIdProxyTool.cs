using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to make proxied requests to downstream services through NyxID.</summary>
public sealed class NyxIdProxyTool : IAgentTool
{
    private readonly NyxIdApiClient _client;
    private readonly IServiceDiscoveryCache _cache;
    private readonly ILogger _logger;

    public NyxIdProxyTool(NyxIdApiClient client, IServiceDiscoveryCache? cache = null, ILogger? logger = null)
    {
        _client = client;
        _cache = cache ?? new InMemoryServiceDiscoveryCache();
        _logger = logger ?? NullLogger.Instance;
    }

    public string Name => "nyxid_proxy";

    public string Description =>
        "Make HTTP requests to downstream services through NyxID's credential-injecting proxy. " +
        "NyxID automatically injects the user's stored credentials. " +
        "Omit slug to discover all proxyable services. " +
        "Provide slug + path to send a proxied request.";

    /// <summary>
    /// No Aevatar-side approval needed. NyxID's proxy layer handles approval
    /// enforcement server-side: when a service has approval enabled, NyxID
    /// blocks the proxy request, sends a push notification (Telegram/FCM/APNs),
    /// and waits for the user to approve before completing the request.
    /// The proxy response may take 30+ seconds during approval wait.
    /// </summary>
    public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "slug": {
              "type": "string",
              "description": "Service slug (e.g. 'llm-openai', 'api-github'). Omit to list all proxyable services."
            },
            "path": {
              "type": "string",
              "description": "API path relative to the service's base URL (e.g. '/chat/completions', '/getMe')"
            },
            "method": {
              "type": "string",
              "enum": ["GET", "POST", "PUT", "PATCH", "DELETE"],
              "description": "HTTP method (default: GET)"
            },
            "body": {
              "type": "string",
              "description": "Request body as JSON string (for POST/PUT/PATCH)"
            },
            "headers": {
              "type": "object",
              "additionalProperties": { "type": "string" },
              "description": "Additional HTTP headers"
            }
          }
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        var orgToken = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdOrgToken);
        if (string.IsNullOrWhiteSpace(token))
            return AnnotateWithToolStatus("""{"error":"No NyxID access token available. User must be authenticated."}""");

        _logger.LogDebug("[nyxid_proxy] Raw arguments: {Args}", argumentsJson);

        var args = ToolArgs.Parse(argumentsJson);
        if (args.HasParseError)
        {
            _logger.LogWarning("[nyxid_proxy] Argument parse failed: {Error}, raw={Raw}", args.ParseError, args.Raw);
            return AnnotateWithToolStatus($"{{\"error\":\"Failed to parse tool arguments\",\"detail\":{System.Text.Json.JsonSerializer.Serialize(args.ParseError)},\"received\":{System.Text.Json.JsonSerializer.Serialize(args.Raw)}}}");
        }

        var slug = args.Str("slug") ?? args.Str("service");
        var path = args.Str("path");
        var method = args.Str("method", "GET");
        var body = args.RawOrStr("body");
        var headers = args.Headers();

        // No slug → discover mode: merge services from both tokens
        if (string.IsNullOrWhiteSpace(slug))
        {
            _logger.LogInformation("[nyxid_proxy] No slug provided, returning service discovery. raw={Raw}", args.Raw);
            return await DiscoverMergedServicesAsync(token, orgToken, ct);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("[nyxid_proxy] Missing path. slug={Slug}, raw={Raw}", slug, args.Raw);
            return AnnotateWithToolStatus($"{{\"error\":\"'path' is required when 'slug' is provided\",\"received\":{args.Raw}}}");
        }

        // Resolve which token owns the target service: user token first, fallback to org token
        var effectiveToken = await ResolveTokenForServiceAsync(token, orgToken, slug, ct);

        _logger.LogInformation("[nyxid_proxy] {Method} slug={Slug} path={Path} tokenSource={Source}",
            method, slug, path, effectiveToken == token ? "user" : "org");
        var result = await _client.ProxyRequestAsync(effectiveToken, slug, path, method, body, headers, ct);

        if (IsApprovalError(result, out var approvalCode, out var approvalRequestId))
        {
            _logger.LogInformation(
                "[nyxid_proxy] Approval response: code={Code} requestId={RequestId}",
                approvalCode, approvalRequestId);
        }

        // Annotate the response with a structural status marker so downstream consumers
        // (LLM prompt + tool-call middleware) can distinguish a real 2xx-with-data response
        // from a NyxID-wrapped 4xx/5xx envelope or an approval-blocked call. Without this,
        // a transient proxy failure surfaces as JSON the LLM happily folds into its
        // "no activity" template — see issue #439.
        return AnnotateWithToolStatus(result);
    }

    /// <summary>
    /// Field name injected at the top of JSON-object responses to record whether the proxy
    /// call was a structural success or an error envelope. Consumers (LLM prompt language,
    /// SkillRunner failure-counting middleware) match on this exact key.
    /// </summary>
    public const string ToolStatusFieldName = "_aevatar_tool_status";

    /// <summary>Status assigned to NyxID error envelopes (HTTP non-2xx, approval-blocked, exception).</summary>
    public const string ToolStatusError = "error";

    /// <summary>Status assigned to plausible 2xx success responses.</summary>
    public const string ToolStatusOk = "ok";

    /// <summary>
    /// Inject <see cref="ToolStatusFieldName"/> at the top of a JSON-object response. Returns
    /// the input unchanged when the body isn't a JSON object (raw text, arrays, etc.) — the
    /// marker is opportunistic, never destructive. Detection rules:
    /// - <c>"error"</c> property truthy → error (matches <see cref="NyxIdApiClient.SendAsync"/>'s
    ///   non-2xx wrapper and exception wrapper)
    /// - <c>"code"</c> numeric and non-zero → error (matches NyxID approval codes 7000/7001 and
    ///   any business-error envelope using the same shape)
    /// </summary>
    internal static string AnnotateWithToolStatus(string? response)
    {
        if (string.IsNullOrEmpty(response))
            return response ?? string.Empty;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(response);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return response;

            // Don't double-annotate (e.g., when the underlying tool already emitted the marker).
            if (doc.RootElement.TryGetProperty(ToolStatusFieldName, out _))
                return response;

            var status = ClassifyToolStatus(doc.RootElement);

            using var stream = new System.IO.MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString(ToolStatusFieldName, status);
                foreach (var prop in doc.RootElement.EnumerateObject())
                    prop.WriteTo(writer);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (System.Text.Json.JsonException)
        {
            // Non-JSON body (raw text, partial stream). The marker is opportunistic — leave
            // the original response intact so existing consumers that depend on the verbatim
            // payload aren't broken.
            return response;
        }
    }

    private static string ClassifyToolStatus(System.Text.Json.JsonElement root)
    {
        if (root.TryGetProperty("error", out var errorProp) && IsTruthyError(errorProp))
            return ToolStatusError;

        if (root.TryGetProperty("code", out var codeProp)
            && codeProp.ValueKind == System.Text.Json.JsonValueKind.Number
            && codeProp.TryGetInt64(out var code)
            && code != 0)
        {
            return ToolStatusError;
        }

        return ToolStatusOk;
    }

    private static bool IsTruthyError(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Null => false,
        // Strings, numbers, objects, arrays under "error" all indicate an error envelope of
        // some kind — the bare presence of a non-false "error" payload is the signal.
        _ => true,
    };

    // ─── Dual-token service discovery + routing ───

    /// <summary>
    /// Discover services from both user and org tokens, merge into a single list.
    /// Deduplicates by slug (user takes precedence).
    /// </summary>
    private async Task<string> DiscoverMergedServicesAsync(
        string userToken, string? orgToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orgToken) || orgToken == userToken)
            return await _client.DiscoverProxyServicesAsync(userToken, ct);

        var userServicesJson = await _client.DiscoverProxyServicesAsync(userToken, ct);
        var orgServicesJson = await _client.DiscoverProxyServicesAsync(orgToken, ct);

        try
        {
            using var userDoc = System.Text.Json.JsonDocument.Parse(userServicesJson);
            using var orgDoc = System.Text.Json.JsonDocument.Parse(orgServicesJson);

            var userSlugs = ParseServiceSlugs(userDoc);
            var merged = new List<System.Text.Json.JsonElement>();

            if (userDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var svc in userDoc.RootElement.EnumerateArray())
                    merged.Add(svc);
            }

            // Add org services that don't collide with user services
            if (orgDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var svc in orgDoc.RootElement.EnumerateArray())
                {
                    if (svc.TryGetProperty("slug", out var slugProp))
                    {
                        var s = slugProp.GetString() ?? string.Empty;
                        if (!userSlugs.Contains(s))
                            merged.Add(svc);
                    }
                }
            }

            // Populate cache with both service lists
            _cache.SetSlugs(HashToken(userToken), userSlugs);
            _cache.SetSlugs(HashToken(orgToken), ParseServiceSlugs(orgDoc));

            return System.Text.Json.JsonSerializer.Serialize(merged);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(ex, "[nyxid_proxy] Failed to merge service lists, returning user services only");
            return userServicesJson;
        }
    }

    /// <summary>
    /// Resolve which token to use for a given service slug.
    /// Checks user token's service list first; falls back to org token.
    /// </summary>
    private async Task<string> ResolveTokenForServiceAsync(
        string userToken, string? orgToken, string slug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orgToken) || orgToken == userToken)
            return userToken;

        if (await ServiceExistsForTokenAsync(userToken, slug, ct))
            return userToken;

        if (await ServiceExistsForTokenAsync(orgToken, slug, ct))
        {
            _logger.LogInformation(
                "[nyxid_proxy] Service {Slug} not found for user token, using org token", slug);
            return orgToken;
        }

        // Neither has it — use user token and let NyxID return the error
        return userToken;
    }

    /// <summary>
    /// Check whether a given token can access a service by slug.
    /// Uses cache first, falls back to live discovery.
    /// </summary>
    private async Task<bool> ServiceExistsForTokenAsync(
        string token, string slug, CancellationToken ct)
    {
        var hash = HashToken(token);

        // Check cache first
        var cached = _cache.GetSlugs(hash);
        if (cached != null)
            return cached.Contains(slug);

        // Cache miss — fetch and cache
        try
        {
            var servicesJson = await _client.DiscoverProxyServicesAsync(token, ct);
            using var doc = System.Text.Json.JsonDocument.Parse(servicesJson);
            var slugs = ParseServiceSlugs(doc);
            _cache.SetSlugs(hash, slugs);
            return slugs.Contains(slug);
        }
        catch
        {
            return false;
        }
    }

    // ─── Helpers ───

    /// <summary>
    /// Extract service slugs from a NyxID /proxy/services JSON response.
    /// </summary>
    internal static HashSet<string> ParseServiceSlugs(System.Text.Json.JsonDocument doc)
    {
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var svc in EnumerateServiceItems(doc.RootElement))
        {
            if (svc.TryGetProperty("slug", out var slugProp))
            {
                var s = slugProp.GetString();
                if (!string.IsNullOrEmpty(s))
                    slugs.Add(s);
            }
        }

        return slugs;
    }

    private static IEnumerable<System.Text.Json.JsonElement> EnumerateServiceItems(System.Text.Json.JsonElement root)
    {
        if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;
            yield break;
        }

        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            yield break;

        foreach (var propertyName in new[] { "services", "custom_services", "data" })
        {
            if (!root.TryGetProperty(propertyName, out var items) ||
                items.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
                yield return item;
        }
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>
    /// Detect NyxID approval error codes (7000 = approval_required, 7001 = approval_failed).
    /// </summary>
    private static bool IsApprovalError(string result, out int code, out string? requestId)
    {
        code = 0;
        requestId = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(result);
            if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number)
                code = c.GetInt32();
            if (doc.RootElement.TryGetProperty("approval_request_id", out var rid))
                requestId = rid.GetString();
            return code is 7000 or 7001;
        }
        catch
        {
            return false;
        }
    }
}
