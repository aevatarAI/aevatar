using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using Aevatar.AI.ToolProviders.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>
/// Ornn skill API client. Routes through NyxID's proxy so the Ornn upstream URL stays a
/// runtime concern (resolved by NyxID from the user's bound <c>ornn-api</c> service) rather
/// than a hardcoded constant. The public Ornn frontend URL only serves the SPA shell, so
/// direct calls return HTML for any path; the NyxID-routed path is the canonical surface
/// (issue #530 follow-up).
/// </summary>
public sealed class OrnnSkillClient
{
    private readonly NyxIdApiClient _nyxApi;
    private readonly OrnnOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Default per-call timeout for Ornn HTTP fetches through the NyxID proxy. Without this, a
    /// stuck upstream call can hold an Orleans grain turn captive for minutes. Successful calls
    /// complete in ~1s, so 30s leaves generous headroom while surfacing the upstream failure
    /// quickly enough that the LLM can recover through skill-search/fallback instructions
    /// instead of silently waiting on one blocked HTTP request.
    /// </summary>
    public static readonly TimeSpan DefaultPerCallTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _perCallTimeout;

    public OrnnSkillClient(OrnnOptions options, NyxIdApiClient nyxApi, ILogger<OrnnSkillClient>? logger = null)
        : this(options, nyxApi, DefaultPerCallTimeout, logger)
    {
    }

    /// <summary>
    /// Test-friendly overload: allows injecting a shorter per-call timeout so timeout behavior
    /// can be verified deterministically without sleeping 30s. Production code should use the
    /// primary constructor and accept <see cref="DefaultPerCallTimeout"/>.
    /// </summary>
    public OrnnSkillClient(
        OrnnOptions options,
        NyxIdApiClient nyxApi,
        TimeSpan perCallTimeout,
        ILogger<OrnnSkillClient>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nyxApi = nyxApi ?? throw new ArgumentNullException(nameof(nyxApi));
        if (perCallTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(perCallTimeout), "Per-call timeout must be positive.");
        _perCallTimeout = perCallTimeout;
        _logger = logger ?? NullLogger<OrnnSkillClient>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Search skills.</summary>
    /// <param name="scope">
    /// Ornn API visibility scope (<c>public/private/mixed/shared-with-me/mine</c>). This is the
    /// upstream query parameter, NOT a model-facing contract: the discovery tool
    /// (<see cref="OrnnSearchSkillsTool"/>) deliberately does not expose it and always uses the
    /// default <c>mixed</c> (the caller's full accessible set) so a model can't narrow visibility
    /// and hide usable skills. Kept here as a faithful seam over the Ornn API for any future
    /// non-discovery caller (e.g. a management tool).
    /// </param>
    public async Task<OrnnSearchResult> SearchSkillsAsync(
        string accessToken,
        string query = "",
        string scope = "mixed",
        int page = 1,
        int pageSize = 20,
        string mode = "keyword",
        CancellationToken ct = default)
    {
        var normalizedMode = string.Equals(mode, "semantic", StringComparison.OrdinalIgnoreCase)
            ? "semantic"
            : "keyword";
        var normalizedScope = scope.ToLowerInvariant() is "public" or "private" or "mixed"
            ? scope.ToLowerInvariant()
            : "mixed";
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var path = $"/api/v1/skill-search?query={Uri.EscapeDataString(query)}&mode={normalizedMode}&scope={Uri.EscapeDataString(normalizedScope)}&page={page}&pageSize={pageSize}";

        using var timeoutCts = new CancellationTokenSource(_perCallTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var response = await _nyxApi.ProxyRequestAsync(
                token: accessToken,
                slug: _options.NyxIdSlug,
                path: path,
                method: "GET",
                body: null,
                extraHeaders: null,
                ct: linkedCts.Token);

            if (TryUnwrapNyxIdProxyError(response, out var proxyError))
                return new OrnnSearchResult { Items = [], Error = proxyError.Detail };

            var envelope = JsonSerializer.Deserialize<OrnnApiResponse<OrnnSearchResult>>(response, JsonOptions);
            return envelope?.Data ?? new OrnnSearchResult { Items = [] };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation is a control-flow signal; let it propagate so the outer LLM
            // run can react instead of seeing a synthetic "no skills" result.
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Our per-call budget fired (caller didn't cancel). Distinguish from generic failure
            // so log dashboards surface upstream slowness as its own signal.
            _logger.LogWarning(
                "Ornn skill search exceeded {TimeoutSeconds}s per-call budget for query '{Query}'",
                (int)_perCallTimeout.TotalSeconds,
                query);
            return new OrnnSearchResult
            {
                Items = [],
                Error = $"Ornn skill search exceeded {(int)_perCallTimeout.TotalSeconds}s budget.",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ornn skill search failed for query '{Query}'", query);
            return new OrnnSearchResult { Items = [], Error = ex.Message };
        }
    }

    public Task<OrnnExactSkillReadResult<OrnnExactSkillDetail>> GetExactSkillDetailAsync(
        string accessToken,
        string guid,
        string literalVersion,
        CancellationToken ct = default)
    {
        ValidateExactReference(guid, literalVersion, nameof(guid));
        return GetExactAsync<OrnnExactSkillDetail>(
            accessToken,
            $"/api/v1/skills/{Uri.EscapeDataString(guid)}?version={Uri.EscapeDataString(literalVersion)}",
            guid,
            ct);
    }

    public Task<OrnnExactSkillReadResult<OrnnSkillJson>> GetExactSkillJsonAsync(
        string accessToken,
        string guid,
        string literalVersion,
        CancellationToken ct = default)
    {
        ValidateExactReference(guid, literalVersion, nameof(guid));
        return GetExactAsync<OrnnSkillJson>(
            accessToken,
            $"/api/v1/skills/{Uri.EscapeDataString(guid)}/json?version={Uri.EscapeDataString(literalVersion)}",
            guid,
            ct);
    }

    private async Task<OrnnExactSkillReadResult<T>> GetExactAsync<T>(
        string accessToken,
        string path,
        string guid,
        CancellationToken ct)
        where T : class
    {
        using var timeoutCts = new CancellationTokenSource(_perCallTimeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            var response = await _nyxApi.ProxyRequestAsync(
                token: accessToken,
                slug: _options.NyxIdSlug,
                path,
                method: "GET",
                body: null,
                extraHeaders: null,
                ct: linkedCts.Token);
            if (TryUnwrapNyxIdProxyError(response, out var proxyError))
                return OrnnExactSkillReadResult<T>.ProxyFailure(proxyError.Status, proxyError.Detail);

            var envelope = JsonSerializer.Deserialize<OrnnApiResponse<T>>(response, JsonOptions);
            return OrnnExactSkillReadResult<T>.Success(envelope?.Data);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Ornn exact skill read exceeded {TimeoutSeconds}s per-call budget for guid '{Guid}'",
                (int)_perCallTimeout.TotalSeconds,
                guid);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ornn exact skill read failed for guid '{Guid}'", guid);
            throw;
        }
    }

    /// <summary>Fetch skill JSON including file contents.</summary>
    public async Task<OrnnSkillJson?> GetSkillJsonAsync(
        string accessToken,
        string idOrName,
        CancellationToken ct = default)
    {
        var path = $"/api/v1/skills/{Uri.EscapeDataString(idOrName)}/json";

        using var timeoutCts = new CancellationTokenSource(_perCallTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var response = await _nyxApi.ProxyRequestAsync(
                token: accessToken,
                slug: _options.NyxIdSlug,
                path: path,
                method: "GET",
                body: null,
                extraHeaders: null,
                ct: linkedCts.Token);

            if (TryUnwrapNyxIdProxyError(response, out var proxyError))
            {
                if (proxyError.Status == 403)
                    throw RemoteSkillFetchException.AccessDenied(idOrName, proxyError.Detail, proxyError.Status);

                if (proxyError.Status == 404)
                    return null;

                throw RemoteSkillFetchException.Unavailable(
                    idOrName,
                    proxyError.Detail,
                    proxyError.Status);
            }

            var envelope = JsonSerializer.Deserialize<OrnnApiResponse<OrnnSkillJson>>(response, JsonOptions);
            return envelope?.Data;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation is a control-flow signal; let it propagate so the outer LLM
            // run can react instead of seeing a synthetic "skill not found" result.
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Our per-call budget fired (caller didn't cancel). Distinguish from generic failure
            // so log dashboards surface upstream slowness as its own signal.
            _logger.LogWarning(
                "Ornn get skill exceeded {TimeoutSeconds}s per-call budget for '{IdOrName}'",
                (int)_perCallTimeout.TotalSeconds,
                idOrName);
            throw RemoteSkillFetchException.Unavailable(
                idOrName,
                $"Remote skill '{idOrName}' timed out while loading.");
        }
        catch (RemoteSkillFetchException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ornn get skill failed for '{IdOrName}'", idOrName);
            throw RemoteSkillFetchException.Unavailable(idOrName, string.Empty);
        }
    }

    /// <summary>
    /// Fetch a skillset (by stable guid or by name) including its member list. The overlay source
    /// resolves the host-configured set name to its guid once and then reads members by that guid,
    /// so a later same-named squatter set cannot hijack the overlay (issue #2498). Member bodies are
    /// refs; callers fetch each member's SKILL.md and its <c>overlay-scope-*</c> tag via
    /// <see cref="GetSkillJsonAsync"/>.
    /// </summary>
    public async Task<OrnnSkillSet?> GetSkillSetAsync(
        string accessToken,
        string idOrName,
        CancellationToken ct = default)
    {
        var path = $"/api/v1/skillsets/{Uri.EscapeDataString(idOrName)}";

        using var timeoutCts = new CancellationTokenSource(_perCallTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var response = await _nyxApi.ProxyRequestAsync(
                token: accessToken,
                slug: _options.NyxIdSlug,
                path: path,
                method: "GET",
                body: null,
                extraHeaders: null,
                ct: linkedCts.Token);

            if (TryUnwrapNyxIdProxyError(response, out var proxyError))
            {
                if (proxyError.Status == 403)
                    throw RemoteSkillFetchException.AccessDenied(idOrName, proxyError.Detail, proxyError.Status);

                return null;
            }

            var envelope = JsonSerializer.Deserialize<OrnnApiResponse<OrnnSkillSet>>(response, JsonOptions);
            return envelope?.Data;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Ornn get skillset exceeded {TimeoutSeconds}s per-call budget for '{IdOrName}'",
                (int)_perCallTimeout.TotalSeconds,
                idOrName);
            return null;
        }
        catch (RemoteSkillFetchException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ornn get skillset failed for '{IdOrName}'", idOrName);
            return null;
        }
    }

    public Task<OrnnSkillSet?> GetExactSkillSetAsync(
        string accessToken,
        string skillsetGuid,
        string literalVersion,
        CancellationToken ct = default)
    {
        ValidateExactReference(skillsetGuid, literalVersion, nameof(skillsetGuid));
        var path = $"/api/v1/skillsets/{Uri.EscapeDataString(skillsetGuid)}?version={Uri.EscapeDataString(literalVersion)}";
        return GetExactAsync<OrnnSkillSet>(accessToken, path, "skillset", skillsetGuid, literalVersion, ct);
    }

    public Task<OrnnSkillSetClosure?> GetExactSkillSetClosureAsync(
        string accessToken,
        string skillsetGuid,
        string literalVersion,
        CancellationToken ct = default)
    {
        ValidateExactReference(skillsetGuid, literalVersion, nameof(skillsetGuid));
        var path = $"/api/v1/skillsets/{Uri.EscapeDataString(skillsetGuid)}/closure?version={Uri.EscapeDataString(literalVersion)}";
        return GetExactAsync<OrnnSkillSetClosure>(accessToken, path, "skillset closure", skillsetGuid, literalVersion, ct);
    }

    public async Task<OrnnSkillSetPublishResponse> CreateSkillSetAsync(
        string accessToken,
        OrnnSkillSetPublishRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var timeoutCts = new CancellationTokenSource(_perCallTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var response = await _nyxApi.ProxyRequestAsync(
                token: accessToken,
                slug: _options.NyxIdSlug,
                path: "/api/v1/skillsets",
                method: "POST",
                body: JsonSerializer.Serialize(request, JsonOptions),
                extraHeaders: null,
                ct: linkedCts.Token);
            if (TryUnwrapNyxIdProxyError(response, out var proxyError))
                return new OrnnSkillSetPublishResponse(false, null, proxyError.Detail);

            var envelope = JsonSerializer.Deserialize<OrnnApiResponse<OrnnSkillSet>>(response, JsonOptions);
            return envelope?.Data is null
                ? new OrnnSkillSetPublishResponse(false, null, envelope?.Error ?? "Ornn returned no skillset.")
                : new OrnnSkillSetPublishResponse(true, envelope.Data);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Ornn skillset publish failed for '{SkillsetName}'", request.Name);
            return new OrnnSkillSetPublishResponse(false, null, ex.Message);
        }
    }

    public async Task<OrnnSkillPublishResponse> PublishSkillAsync(
        string accessToken,
        byte[] zipBytes,
        CancellationToken ct = default)
    {
        using var timeoutCts = new CancellationTokenSource(_perCallTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var response = await _nyxApi.ProxyRequestBinaryAsync(
                token: accessToken,
                slug: _options.NyxIdSlug,
                path: "/api/v1/skills",
                method: "POST",
                body: zipBytes,
                contentType: "application/zip",
                extraHeaders: null,
                ct: linkedCts.Token);

            if (TryUnwrapNyxIdProxyError(response, out var proxyError))
                return new OrnnSkillPublishResponse(false, response, proxyError.Detail);

            return new OrnnSkillPublishResponse(true, response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Ornn skill publish exceeded {TimeoutSeconds}s per-call budget",
                (int)_perCallTimeout.TotalSeconds);
            return new OrnnSkillPublishResponse(
                false,
                string.Empty,
                $"Ornn skill publish exceeded {(int)_perCallTimeout.TotalSeconds}s budget.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ornn skill publish failed");
            return new OrnnSkillPublishResponse(false, string.Empty, ex.Message);
        }
    }

    public async Task<OrnnSkillPublishResponse> UpdateSkillAsync(
        string accessToken,
        string skillId,
        byte[] zipBytes,
        CancellationToken ct = default)
    {
        var path = $"/api/v1/skills/{Uri.EscapeDataString(skillId)}";
        using var timeoutCts = new CancellationTokenSource(_perCallTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var response = await _nyxApi.ProxyRequestBinaryAsync(
                token: accessToken,
                slug: _options.NyxIdSlug,
                path: path,
                method: "PUT",
                body: zipBytes,
                contentType: "application/zip",
                extraHeaders: null,
                ct: linkedCts.Token);

            if (TryUnwrapNyxIdProxyError(response, out var proxyError))
                return new OrnnSkillPublishResponse(false, response, proxyError.Detail);

            return new OrnnSkillPublishResponse(true, response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Ornn skill update exceeded {TimeoutSeconds}s per-call budget for '{SkillId}'",
                (int)_perCallTimeout.TotalSeconds,
                skillId);
            return new OrnnSkillPublishResponse(
                false,
                string.Empty,
                $"Ornn skill update exceeded {(int)_perCallTimeout.TotalSeconds}s budget.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ornn skill update failed for '{SkillId}'", skillId);
            return new OrnnSkillPublishResponse(false, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// Detect the wrapped error envelope NyxIdApiClient.SendAsync emits when the upstream
    /// returns non-2xx (<c>{"error": true, "status": N, "body": "..."}</c>) so callers see a
    /// concise actionable message instead of a JsonException about the wrapper shape.
    /// </summary>
    private bool TryUnwrapNyxIdProxyError(string response, out NyxIdProxyError error)
    {
        error = new NyxIdProxyError(0, string.Empty);
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("error", out var errorProp) ||
                errorProp.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            var status = root.TryGetProperty("status", out var statusProp) &&
                         statusProp.ValueKind == JsonValueKind.Number
                ? statusProp.GetInt32()
                : 0;
            var upstreamDetail = TryExtractUpstreamOrnnReason(root);

            // 404 here means NyxID could not resolve `_options.NyxIdSlug` to an upstream: either
            // the user has not bound an Ornn service to this slug, or the deployment's NyxID
            // catalog uses a different slug name. The LLM can recover by guiding the user to
            // bind the service or by retrying with a different slug; surface that hint instead
            // of a bare "status=404".
            var detail = status switch
            {
                403 when !string.IsNullOrWhiteSpace(upstreamDetail) => upstreamDetail,
                403 => BuildProxyScopeAccessDeniedDetail(),
                404 => $"Ornn skill API not reachable: NyxID has no service bound to slug '{_options.NyxIdSlug}'. " +
                       "The user may need to connect their Ornn account via NyxID (nyxid_services action=create), " +
                       "or the deployment may need to override Aevatar:Ornn:NyxIdSlug.",
                _ => $"NyxID proxy returned status={status}.",
            };
            error = new NyxIdProxyError(status, detail);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string BuildProxyScopeAccessDeniedDetail() =>
        $"Ornn skill API access denied through NyxID proxy slug '{_options.NyxIdSlug}'. " +
        "The API key is missing proxy scope or service authorization for the Ornn UserService. " +
        "Reconnect the Ornn service in NyxID and recreate or rotate the scheduled agent key.";

    private static string? TryExtractUpstreamOrnnReason(JsonElement root)
    {
        var body = root.TryGetProperty("body", out var bodyProp) && bodyProp.ValueKind == JsonValueKind.String
            ? bodyProp.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var trimmed = body.Trim();
        if (!trimmed.StartsWith('{'))
            return trimmed;

        try
        {
            using var bodyDocument = JsonDocument.Parse(trimmed);
            var bodyRoot = bodyDocument.RootElement;
            if (bodyRoot.ValueKind != JsonValueKind.Object)
                return trimmed;

            if (bodyRoot.TryGetProperty("error", out var errorProp) &&
                errorProp.ValueKind == JsonValueKind.Object)
            {
                var code = TryReadString(errorProp, "code");
                var message = TryReadString(errorProp, "message");
                if (!string.IsNullOrWhiteSpace(message))
                    return string.IsNullOrWhiteSpace(code) ? message : $"{code}: {message}";
            }

            return TryReadString(bodyRoot, "message") ??
                   TryReadString(bodyRoot, "detail");
        }
        catch (JsonException)
        {
            return trimmed;
        }
    }

    private static string? TryReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(prop.GetString())
            ? prop.GetString()
            : null;

    private async Task<T?> GetExactAsync<T>(
        string accessToken,
        string path,
        string resourceKind,
        string guid,
        string literalVersion,
        CancellationToken ct)
        where T : class
    {
        using var timeoutCts = new CancellationTokenSource(_perCallTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var response = await _nyxApi.ProxyRequestAsync(
                token: accessToken,
                slug: _options.NyxIdSlug,
                path: path,
                method: "GET",
                body: null,
                extraHeaders: null,
                ct: linkedCts.Token);
            if (TryUnwrapNyxIdProxyError(response, out _))
                return null;

            return JsonSerializer.Deserialize<OrnnApiResponse<T>>(response, JsonOptions)?.Data;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Ornn exact {ResourceKind} read failed for guid={Guid} version={LiteralVersion}",
                resourceKind,
                guid,
                literalVersion);
            return null;
        }
    }

    private static void ValidateExactReference(string guid, string literalVersion, string parameterName)
    {
        if (!Guid.TryParseExact(guid, "D", out var parsedGuid) ||
            !string.Equals(parsedGuid.ToString("D"), guid, StringComparison.Ordinal))
            throw new ArgumentException("Exact Ornn references require a canonical GUID.", parameterName);
        if (string.IsNullOrWhiteSpace(literalVersion) ||
            literalVersion.Split('.', StringSplitOptions.None) is not [var major, var minor] ||
            !int.TryParse(major, out var majorValue) ||
            !int.TryParse(minor, out var minorValue) ||
            majorValue < 0 || minorValue < 0 ||
            !string.Equals(majorValue.ToString(), major, StringComparison.Ordinal) ||
            !string.Equals(minorValue.ToString(), minor, StringComparison.Ordinal))
        {
            throw new ArgumentException("Exact Ornn references require a literal major.minor version.", nameof(literalVersion));
        }
    }

    private sealed record NyxIdProxyError(int Status, string Detail);
}

// DTOs

public sealed class OrnnApiResponse<T>
{
    public T? Data { get; set; }
    public string? Error { get; set; }
}

public sealed class OrnnSearchResult
{
    public string? SearchMode { get; set; }
    public string? SearchScope { get; set; }
    public int Total { get; set; }
    public int TotalPages { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<OrnnSkillSummary> Items { get; set; } = [];
    [JsonIgnore] public string? Error { get; set; }
}

public sealed class OrnnSkillSummary
{
    public string? Guid { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool IsPrivate { get; set; }
    public List<string>? Tags { get; set; }
    public OrnnSkillMetadata? Metadata { get; set; }
}

public sealed class OrnnExactSkillDetail
{
    public string? Guid { get; set; }
    public string? Name { get; set; }
    public string? SkillHash { get; set; }
    public string? CreatedBy { get; set; }
}

public sealed record OrnnExactSkillReadResult<T>(T? Value, int? ProxyStatus, string? FailureDetail)
    where T : class
{
    public static OrnnExactSkillReadResult<T> Success(T? value) => new(value, null, null);

    public static OrnnExactSkillReadResult<T> ProxyFailure(int status, string detail) =>
        new(null, status, detail);
}

public sealed class OrnnSkillMetadata
{
    public string? Category { get; set; }
    [JsonPropertyName("tag")]
    public List<string>? Tags { get; set; }
    public List<OrnnSkillToolDeclaration>? Tools { get; set; }
}

public sealed class OrnnSkillToolDeclaration
{
    public string? Tool { get; set; }
    public string? Type { get; set; }
}

public sealed class OrnnSkillJson
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }
    public OrnnSkillMetadata? Metadata { get; set; }
    public Dictionary<string, string>? Files { get; set; }
}

/// <summary>A curated Ornn skillset. Its <see cref="Members"/> are references; fetch each body via
/// <see cref="OrnnSkillClient.GetSkillJsonAsync"/>.</summary>
public sealed class OrnnSkillSet
{
    public string? Guid { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? LatestVersion { get; set; }
    public string? CreatedBy { get; set; }
    /// <summary>Set-level master prompt authored on the skillset itself.</summary>
    public string? Instructions { get; set; }
    public bool IsPrivate { get; set; }
    public List<OrnnSkillSetMember> Members { get; set; } = [];
}

public sealed class OrnnSkillSetClosure
{
    public string? Instructions { get; set; }
    public List<OrnnSkillSetClosureItem> Items { get; set; } = [];
}

public sealed class OrnnSkillSetClosureItem
{
    public string? Guid { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
}

public sealed record OrnnSkillSetPublishRequest(
    string Name,
    string Description,
    string Instructions,
    string Kind,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Members,
    string Version);

public sealed record OrnnSkillSetPublishResponse(
    bool Succeeded,
    OrnnSkillSet? Skillset,
    string? Error = null);

/// <summary>
/// One skillset member. The upstream serializes members either as <c>"name@version"</c> strings or as
/// objects (<c>{ guid, name, version }</c>); <see cref="OrnnSkillSetMemberJsonConverter"/> accepts both.
/// Only the fetch <see cref="Reference"/> is load-bearing — the member's overlay-scope tag and body are
/// read from the fetched skill JSON, not from the set entry, so the set's member shape stays irrelevant.
/// </summary>
[JsonConverter(typeof(OrnnSkillSetMemberJsonConverter))]
public sealed class OrnnSkillSetMember
{
    public string? Guid { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }

    /// <summary>The id or name to fetch this member's full skill JSON with (guid preferred).</summary>
    public string? Reference =>
        !string.IsNullOrWhiteSpace(Guid) ? Guid :
        string.IsNullOrWhiteSpace(Name) ? null : Name;
}

/// <summary>Reads a skillset member from either a <c>"name@version"</c> string or a <c>{guid,name,version}</c> object.</summary>
internal sealed class OrnnSkillSetMemberJsonConverter : JsonConverter<OrnnSkillSetMember>
{
    public override OrnnSkillSetMember? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return FromReferenceString(reader.GetString());
            case JsonTokenType.StartObject:
                using (var document = JsonDocument.ParseValue(ref reader))
                {
                    var root = document.RootElement;
                    return new OrnnSkillSetMember
                    {
                        Guid = ReadString(root, "guid"),
                        Name = ReadString(root, "name"),
                        Version = ReadString(root, "version"),
                    };
                }
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, OrnnSkillSetMember value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Guid is not null) writer.WriteString("guid", value.Guid);
        if (value.Name is not null) writer.WriteString("name", value.Name);
        if (value.Version is not null) writer.WriteString("version", value.Version);
        writer.WriteEndObject();
    }

    private static OrnnSkillSetMember? FromReferenceString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        var at = trimmed.LastIndexOf('@');
        return at > 0
            ? new OrnnSkillSetMember { Name = trimmed[..at], Version = trimmed[(at + 1)..] }
            : new OrnnSkillSetMember { Name = trimmed };
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals(propertyName) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }
}
