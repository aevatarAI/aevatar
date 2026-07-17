using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;
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
    private const int MaximumDeclaredTools = 50;
    private const int MaximumDirectMembers = 100;
    private const int MaximumClosureNodes = 500;
    private const long EvidenceResponseMaximumBytes = NyxIdToolOptions.DefaultProxyFileArtifactMaxBytes;
    private readonly NyxIdApiClient _nyxApi;
    private readonly OrnnOptions _options;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Regex LiteralVersionPattern = new(
        "^[0-9]+\\.[0-9]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
        ILogger<OrnnSkillClient>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nyxApi = nyxApi ?? throw new ArgumentNullException(nameof(nyxApi));
        if (perCallTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(perCallTimeout), "Per-call timeout must be positive.");
        _perCallTimeout = perCallTimeout;
        _logger = logger ?? NullLogger<OrnnSkillClient>.Instance;
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

                return null;
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
            return null;
        }
        catch (RemoteSkillFetchException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ornn get skill failed for '{IdOrName}'", idOrName);
            return null;
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

    internal async Task<OrnnExactSkillEvidence> GetExactSkillAsync(
        string accessToken,
        Aevatar.AI.Abstractions.ExactRemoteSkillRef reference,
        CancellationToken ct = default)
    {
        ValidateExactReference(
            reference.Guid,
            reference.LiteralVersion,
            ExactRemoteResourceKind.Skill);
        var encodedGuid = Uri.EscapeDataString(reference.Guid);
        var encodedVersion = Uri.EscapeDataString(reference.LiteralVersion);
        var resourceKind = ExactRemoteResourceKind.Skill;

        using var timeoutCts = new CancellationTokenSource(_perCallTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            var packageTask = ReadExactDataAsync<OrnnSkillJson>(
                accessToken,
                $"/api/v1/skills/{encodedGuid}/json?version={encodedVersion}",
                NyxIdToolOptions.HardProxyFileArtifactMaxBytes,
                resourceKind,
                reference.Guid,
                reference.LiteralVersion,
                linkedCts.Token);
            var detailTask = ReadExactDataAsync<OrnnExactSkillDetail>(
                accessToken,
                $"/api/v1/skills/{encodedGuid}?version={encodedVersion}",
                EvidenceResponseMaximumBytes,
                resourceKind,
                reference.Guid,
                reference.LiteralVersion,
                linkedCts.Token);
            var versionsTask = ReadExactDataAsync<OrnnExactVersionItems<OrnnExactSkillVersionRow>>(
                accessToken,
                $"/api/v1/skills/{encodedGuid}/versions",
                EvidenceResponseMaximumBytes,
                resourceKind,
                reference.Guid,
                reference.LiteralVersion,
                linkedCts.Token);

            await Task.WhenAll(packageTask, detailTask, versionsTask);
            return BuildExactSkillEvidence(reference, packageTask.Result, detailTask.Result, versionsTask.Result.Items);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw ExactRemoteFetchException.Unavailable(
                resourceKind,
                reference.Guid,
                reference.LiteralVersion,
                $"the shared {_perCallTimeout.TotalSeconds:0.###} second request budget expired");
        }
    }

    internal async Task<OrnnExactSkillsetEvidence> GetExactSkillsetAsync(
        string accessToken,
        Aevatar.AI.Abstractions.ExactRemoteSkillsetRef reference,
        CancellationToken ct = default)
    {
        ValidateExactReference(
            reference.Guid,
            reference.LiteralVersion,
            ExactRemoteResourceKind.Skillset);
        var encodedGuid = Uri.EscapeDataString(reference.Guid);
        var encodedVersion = Uri.EscapeDataString(reference.LiteralVersion);
        var resourceKind = ExactRemoteResourceKind.Skillset;

        using var timeoutCts = new CancellationTokenSource(_perCallTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            var detailTask = ReadExactDataAsync<OrnnSkillSet>(
                accessToken,
                $"/api/v1/skillsets/{encodedGuid}?version={encodedVersion}",
                EvidenceResponseMaximumBytes,
                resourceKind,
                reference.Guid,
                reference.LiteralVersion,
                linkedCts.Token);
            var closureTask = ReadExactDataAsync<OrnnExactSkillsetClosure>(
                accessToken,
                $"/api/v1/skillsets/{encodedGuid}/closure?version={encodedVersion}",
                EvidenceResponseMaximumBytes,
                resourceKind,
                reference.Guid,
                reference.LiteralVersion,
                linkedCts.Token);
            var versionsTask = ReadExactDataAsync<OrnnExactVersionItems<OrnnExactSkillsetVersionRow>>(
                accessToken,
                $"/api/v1/skillsets/{encodedGuid}/versions",
                EvidenceResponseMaximumBytes,
                resourceKind,
                reference.Guid,
                reference.LiteralVersion,
                linkedCts.Token);

            await Task.WhenAll(detailTask, closureTask, versionsTask);
            return BuildExactSkillsetEvidence(reference, detailTask.Result, closureTask.Result, versionsTask.Result.Items);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw ExactRemoteFetchException.Unavailable(
                resourceKind,
                reference.Guid,
                reference.LiteralVersion,
                $"the shared {_perCallTimeout.TotalSeconds:0.###} second request budget expired");
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

    private async Task<T> ReadExactDataAsync<T>(
        string accessToken,
        string path,
        long maximumBytes,
        ExactRemoteResourceKind resourceKind,
        string guid,
        string literalVersion,
        CancellationToken ct)
        where T : class
    {
        var response = await _nyxApi.ProxyGetBinaryResponseAsync(
            accessToken,
            _options.NyxIdSlug,
            path,
            extraHeaders: null,
            maximumBytes,
            ct);
        if (!response.Succeeded)
        {
            if (response.Detail is "content_length_exceeds_max_bytes" or "content_exceeds_max_bytes")
            {
                throw ExactRemoteFetchException.InvalidResponse(
                    resourceKind,
                    guid,
                    literalVersion,
                    $"response '{path}' exceeded {maximumBytes} bytes");
            }

            throw ExactRemoteFetchException.Unavailable(
                resourceKind,
                guid,
                literalVersion,
                string.IsNullOrWhiteSpace(response.Detail) ? $"request '{path}' failed" : response.Detail,
                response.HttpStatus == 0 ? null : response.HttpStatus);
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<OrnnApiResponse<T>>(response.Content, JsonOptions);
            return envelope?.Data ?? throw ExactRemoteFetchException.InvalidResponse(
                resourceKind,
                guid,
                literalVersion,
                $"response '{path}' omitted data");
        }
        catch (ExactRemoteFetchException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw ExactRemoteFetchException.InvalidResponse(
                resourceKind,
                guid,
                literalVersion,
                $"response '{path}' was not valid JSON",
                ex);
        }
    }

    private static OrnnExactSkillEvidence BuildExactSkillEvidence(
        Aevatar.AI.Abstractions.ExactRemoteSkillRef reference,
        OrnnSkillJson package,
        OrnnExactSkillDetail detail,
        IReadOnlyList<OrnnExactSkillVersionRow> versionRows)
    {
        RequireGuidMatch(detail.Guid, reference.Guid, ExactRemoteResourceKind.Skill, reference.LiteralVersion);
        RequireText(package.Name, "package name", ExactRemoteResourceKind.Skill, reference.Guid, reference.LiteralVersion);
        RequireText(detail.Name, "detail name", ExactRemoteResourceKind.Skill, reference.Guid, reference.LiteralVersion);
        RequireEqual(package.Name!, detail.Name!, "package/detail name", ExactRemoteResourceKind.Skill, reference.Guid, reference.LiteralVersion);
        RequireEqual(package.Version, reference.LiteralVersion, "package version", ExactRemoteResourceKind.Skill, reference.Guid, reference.LiteralVersion);
        RequireEqual(detail.Version, reference.LiteralVersion, "detail version", ExactRemoteResourceKind.Skill, reference.Guid, reference.LiteralVersion);
        if (package.Metadata is null || detail.Metadata is null)
        {
            throw ExactRemoteFetchException.InvalidResponse(
                ExactRemoteResourceKind.Skill,
                reference.Guid,
                reference.LiteralVersion,
                "package and detail metadata must both be present");
        }

        var versionRow = SelectExactVersionRow(
            versionRows,
            reference.LiteralVersion,
            static row => row.Version,
            ExactRemoteResourceKind.Skill,
            reference.Guid);
        RequireText(detail.SkillHash, "detail skill hash", ExactRemoteResourceKind.Skill, reference.Guid, reference.LiteralVersion);
        RequireText(versionRow.SkillHash, "version skill hash", ExactRemoteResourceKind.Skill, reference.Guid, reference.LiteralVersion);
        RequireText(versionRow.Integrity, "version integrity", ExactRemoteResourceKind.Skill, reference.Guid, reference.LiteralVersion);
        RequireEqual(detail.SkillHash!, versionRow.SkillHash!, "detail/version skill hash", ExactRemoteResourceKind.Skill, reference.Guid, reference.LiteralVersion);
        VerifyIntegrity(versionRow.SkillHash!, versionRow.Integrity, reference.Guid, reference.LiteralVersion);

        var packageTools = NormalizeTools(package.Metadata?.Tools, reference.Guid, reference.LiteralVersion);
        var detailTools = NormalizeTools(detail.Metadata?.Tools, reference.Guid, reference.LiteralVersion);
        RequireToolSetsEqual(packageTools, detailTools, reference.Guid, reference.LiteralVersion);
        var exactPackage = ValidatePackage(package.Files, reference.Guid, reference.LiteralVersion);
        package.Files = exactPackage.Files.ToDictionary(static file => file.Key, static file => file.Value, StringComparer.Ordinal);

        return new OrnnExactSkillEvidence(
            package,
            detail.Name!,
            BuildProvenance(versionRow, reference.Guid, reference.LiteralVersion, ExactRemoteResourceKind.Skill),
            exactPackage,
            packageTools);
    }

    private static OrnnExactSkillsetEvidence BuildExactSkillsetEvidence(
        Aevatar.AI.Abstractions.ExactRemoteSkillsetRef reference,
        OrnnSkillSet detail,
        OrnnExactSkillsetClosure closure,
        IReadOnlyList<OrnnExactSkillsetVersionRow> versionRows)
    {
        RequireGuidMatch(detail.Guid, reference.Guid, ExactRemoteResourceKind.Skillset, reference.LiteralVersion);
        RequireText(detail.Name, "detail name", ExactRemoteResourceKind.Skillset, reference.Guid, reference.LiteralVersion);
        RequireEqual(detail.Version, reference.LiteralVersion, "detail version", ExactRemoteResourceKind.Skillset, reference.Guid, reference.LiteralVersion);
        RequireText(detail.Instructions, "detail instructions", ExactRemoteResourceKind.Skillset, reference.Guid, reference.LiteralVersion);
        RequireText(closure.Instructions, "closure instructions", ExactRemoteResourceKind.Skillset, reference.Guid, reference.LiteralVersion);
        RequireEqual(detail.Instructions!, closure.Instructions!, "detail/closure instructions", ExactRemoteResourceKind.Skillset, reference.Guid, reference.LiteralVersion);

        if (detail.Members.Count > MaximumDirectMembers)
        {
            throw ExactRemoteFetchException.InvalidResponse(
                ExactRemoteResourceKind.Skillset,
                reference.Guid,
                reference.LiteralVersion,
                $"direct member count exceeded {MaximumDirectMembers}");
        }
        if (detail.Members.Count == 0)
        {
            throw ExactRemoteFetchException.InvalidResponse(
                ExactRemoteResourceKind.Skillset,
                reference.Guid,
                reference.LiteralVersion,
                "direct members were missing or empty");
        }
        if (closure.Items is null)
        {
            throw ExactRemoteFetchException.InvalidResponse(
                ExactRemoteResourceKind.Skillset,
                reference.Guid,
                reference.LiteralVersion,
                "closure items were missing");
        }
        if (closure.Items.Count > MaximumClosureNodes)
        {
            throw ExactRemoteFetchException.InvalidResponse(
                ExactRemoteResourceKind.Skillset,
                reference.Guid,
                reference.LiteralVersion,
                $"closure node count exceeded {MaximumClosureNodes}");
        }

        var versionRow = SelectExactVersionRow(
            versionRows,
            reference.LiteralVersion,
            static row => row.Version,
            ExactRemoteResourceKind.Skillset,
            reference.Guid);
        if (versionRow.MemberCount is null)
        {
            throw ExactRemoteFetchException.InvalidResponse(
                ExactRemoteResourceKind.Skillset,
                reference.Guid,
                reference.LiteralVersion,
                "version member count was missing");
        }
        if (versionRow.MemberCount.Value != detail.Members.Count)
        {
            throw ExactRemoteFetchException.IntegrityMismatch(
                ExactRemoteResourceKind.Skillset,
                reference.Guid,
                reference.LiteralVersion,
                "version member count differs from detail members");
        }

        var closureItems = ValidateClosureItems(closure.Items, reference.Guid, reference.LiteralVersion);
        var directMembers = ResolveDirectMembers(detail.Members, closureItems, reference.Guid, reference.LiteralVersion);
        return new OrnnExactSkillsetEvidence(
            detail.Name!,
            BuildProvenance(versionRow, reference.Guid, reference.LiteralVersion, ExactRemoteResourceKind.Skillset),
            detail.Instructions!,
            directMembers,
            closureItems.Select(static item => item.Reference).ToArray());
    }

    private static ExactRemotePackage ValidatePackage(
        IReadOnlyDictionary<string, string>? files,
        string guid,
        string literalVersion)
    {
        if (files is null)
        {
            throw ExactRemoteFetchException.InvalidResponse(
                ExactRemoteResourceKind.Skill,
                guid,
                literalVersion,
                "package files were missing");
        }

        var maximum = ExactRemotePackageBounds.AdapterMaximum;
        if (files.Count > maximum.MaximumFileCount)
        {
            throw ExactRemoteFetchException.InvalidResponse(
                ExactRemoteResourceKind.Skill,
                guid,
                literalVersion,
                $"package file count exceeded {maximum.MaximumFileCount}");
        }

        var normalizedFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        var maximumPathBytes = 0;
        long maximumFileBytes = 0;
        long totalFileBytes = 0;
        foreach (var (path, content) in files)
        {
            var normalizedPath = NormalizePackagePath(path, guid, literalVersion);
            if (content is null)
            {
                throw ExactRemoteFetchException.InvalidResponse(
                    ExactRemoteResourceKind.Skill,
                    guid,
                    literalVersion,
                    $"package file '{normalizedPath}' had null content");
            }
            if (!normalizedFiles.TryAdd(normalizedPath, content))
            {
                throw ExactRemoteFetchException.InvalidResponse(
                    ExactRemoteResourceKind.Skill,
                    guid,
                    literalVersion,
                    $"package contains duplicate normalized path '{normalizedPath}'");
            }

            var pathBytes = Encoding.UTF8.GetByteCount(normalizedPath);
            var fileBytes = Encoding.UTF8.GetByteCount(content);
            if (pathBytes > maximum.MaximumPathUtf8Bytes || fileBytes > maximum.MaximumFileUtf8Bytes)
            {
                throw ExactRemoteFetchException.InvalidResponse(
                    ExactRemoteResourceKind.Skill,
                    guid,
                    literalVersion,
                    $"package path or file '{normalizedPath}' exceeded adapter bounds");
            }

            maximumPathBytes = Math.Max(maximumPathBytes, pathBytes);
            maximumFileBytes = Math.Max(maximumFileBytes, fileBytes);
            totalFileBytes += fileBytes;
            if (totalFileBytes > maximum.MaximumTotalFileUtf8Bytes)
            {
                throw ExactRemoteFetchException.InvalidResponse(
                    ExactRemoteResourceKind.Skill,
                    guid,
                    literalVersion,
                    "package total file bytes exceeded adapter bounds");
            }
        }

        return new ExactRemotePackage(
            new ReadOnlyDictionary<string, string>(normalizedFiles),
            new ExactRemotePackageShape(files.Count, maximumPathBytes, maximumFileBytes, totalFileBytes));
    }

    private static string NormalizePackagePath(string path, string guid, string literalVersion)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            throw InvalidPackagePath(guid, literalVersion, path);

        var replaced = path.Replace('\\', '/');
        if (replaced.StartsWith('/') || Regex.IsMatch(replaced, "^[A-Za-z]:/", RegexOptions.CultureInvariant))
            throw InvalidPackagePath(guid, literalVersion, path);

        var segments = replaced.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
            throw InvalidPackagePath(guid, literalVersion, path);
        return string.Join('/', segments);
    }

    private static ExactRemoteFetchException InvalidPackagePath(string guid, string literalVersion, string? path) =>
        ExactRemoteFetchException.InvalidResponse(
            ExactRemoteResourceKind.Skill,
            guid,
            literalVersion,
            $"package path '{path}' is not a normalized relative path");

    private static IReadOnlyList<ExactRemoteToolDeclaration> NormalizeTools(
        IReadOnlyList<OrnnSkillToolDeclaration>? tools,
        string guid,
        string literalVersion)
    {
        if (tools is null)
            return [];
        if (tools.Count > MaximumDeclaredTools)
        {
            throw ExactRemoteFetchException.InvalidResponse(
                ExactRemoteResourceKind.Skill,
                guid,
                literalVersion,
                $"declared tool count exceeded {MaximumDeclaredTools}");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<ExactRemoteToolDeclaration>(tools.Count);
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Tool) || string.IsNullOrWhiteSpace(tool.Type) ||
                !names.Add(tool.Tool))
            {
                throw ExactRemoteFetchException.InvalidResponse(
                    ExactRemoteResourceKind.Skill,
                    guid,
                    literalVersion,
                    "declared tools contain a blank or duplicate identity");
            }

            var mcpServers = new List<ExactRemoteMcpServerDeclaration>();
            var mcpKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var server in tool.McpServers ?? [])
            {
                var key = $"{server.Mcp}\u001f{server.Version}";
                if (string.IsNullOrWhiteSpace(server.Mcp) || string.IsNullOrWhiteSpace(server.Version) ||
                    !mcpKeys.Add(key))
                {
                    throw ExactRemoteFetchException.InvalidResponse(
                        ExactRemoteResourceKind.Skill,
                        guid,
                        literalVersion,
                        $"declared tool '{tool.Tool}' contains a blank or duplicate MCP server");
                }
                mcpServers.Add(new ExactRemoteMcpServerDeclaration(server.Mcp, server.Version));
            }

            normalized.Add(new ExactRemoteToolDeclaration(
                tool.Tool,
                tool.Type,
                mcpServers.OrderBy(static server => server.Mcp, StringComparer.Ordinal)
                    .ThenBy(static server => server.Version, StringComparer.Ordinal)
                    .ToArray()));
        }

        return normalized.OrderBy(static tool => tool.Tool, StringComparer.Ordinal).ToArray();
    }

    private static void RequireToolSetsEqual(
        IReadOnlyList<ExactRemoteToolDeclaration> packageTools,
        IReadOnlyList<ExactRemoteToolDeclaration> detailTools,
        string guid,
        string literalVersion)
    {
        var packageKeys = packageTools.Select(ToolKey).ToHashSet(StringComparer.Ordinal);
        var detailKeys = detailTools.Select(ToolKey).ToHashSet(StringComparer.Ordinal);
        if (!packageKeys.SetEquals(detailKeys))
        {
            throw ExactRemoteFetchException.IntegrityMismatch(
                ExactRemoteResourceKind.Skill,
                guid,
                literalVersion,
                "package/detail declared tools differ");
        }
    }

    private static string ToolKey(ExactRemoteToolDeclaration tool) =>
        $"{tool.Tool}\u001f{tool.Type}\u001f{string.Join('\u001e', tool.McpServers.Select(static server => $"{server.Mcp}\u001d{server.Version}"))}";

    private static void VerifyIntegrity(string skillHash, string? integrity, string guid, string literalVersion)
    {
        byte[] digest;
        try
        {
            digest = Convert.FromHexString(skillHash);
        }
        catch (FormatException ex)
        {
            throw ExactRemoteFetchException.InvalidResponse(
                ExactRemoteResourceKind.Skill,
                guid,
                literalVersion,
                "version skill hash is not hexadecimal",
                ex);
        }

        if (digest.Length != 32)
        {
            throw ExactRemoteFetchException.InvalidResponse(
                ExactRemoteResourceKind.Skill,
                guid,
                literalVersion,
                "version skill hash is not a SHA-256 digest");
        }

        var expected = $"sha256-{Convert.ToBase64String(digest)}";
        if (!string.Equals(integrity, expected, StringComparison.Ordinal))
        {
            throw ExactRemoteFetchException.IntegrityMismatch(
                ExactRemoteResourceKind.Skill,
                guid,
                literalVersion,
                "version integrity does not match the skill hash");
        }
    }

    private static ExactRemoteVersionProvenance BuildProvenance(
        IOrnnExactVersionRow row,
        string guid,
        string literalVersion,
        ExactRemoteResourceKind resourceKind)
    {
        RequireText(row.CreatedBy, "version publisher subject", resourceKind, guid, literalVersion);
        ValidateOptionalSnapshot(row.CreatedByEmail, "publisher email snapshot", resourceKind, guid, literalVersion);
        ValidateOptionalSnapshot(row.CreatedByDisplayName, "publisher display name snapshot", resourceKind, guid, literalVersion);
        if (!DateTimeOffset.TryParse(
                row.CreatedOn,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var publishedAt))
        {
            throw ExactRemoteFetchException.InvalidResponse(
                resourceKind,
                guid,
                literalVersion,
                "version published timestamp is missing or invalid");
        }

        return new ExactRemoteVersionProvenance(
            row.CreatedBy!,
            row.CreatedByEmail,
            row.CreatedByDisplayName,
            publishedAt.ToUniversalTime());
    }

    private static void ValidateOptionalSnapshot(
        string? value,
        string field,
        ExactRemoteResourceKind resourceKind,
        string guid,
        string literalVersion)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
            throw ExactRemoteFetchException.InvalidResponse(resourceKind, guid, literalVersion, $"{field} was blank");
    }

    private static T SelectExactVersionRow<T>(
        IReadOnlyList<T> rows,
        string literalVersion,
        Func<T, string?> versionSelector,
        ExactRemoteResourceKind resourceKind,
        string guid)
    {
        var matches = rows.Where(row => string.Equals(versionSelector(row), literalVersion, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            throw ExactRemoteFetchException.InvalidResponse(
                resourceKind,
                guid,
                literalVersion,
                "versions response must contain the requested literal version exactly once");
        }
        return matches[0];
    }

    private static IReadOnlyList<ValidatedClosureItem> ValidateClosureItems(
        IReadOnlyList<OrnnExactSkillsetClosureItem> items,
        string guid,
        string literalVersion)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var validated = new List<ValidatedClosureItem>(items.Count);
        foreach (var item in items)
        {
            RequireText(item.Ref, "closure ref", ExactRemoteResourceKind.Skillset, guid, literalVersion);
            RequireText(item.Name, "closure name", ExactRemoteResourceKind.Skillset, guid, literalVersion);
            ValidateExactReference(item.Guid, item.Version, ExactRemoteResourceKind.Skillset);
            if (item.Depth is null || item.Depth.Value < 0)
            {
                throw ExactRemoteFetchException.InvalidResponse(
                    ExactRemoteResourceKind.Skillset,
                    guid,
                    literalVersion,
                    "closure depth must be non-negative");
            }

            var normalizedGuid = System.Guid.Parse(item.Guid!).ToString("D");
            if (!identities.Add(normalizedGuid))
            {
                throw ExactRemoteFetchException.InvalidResponse(
                    ExactRemoteResourceKind.Skillset,
                    guid,
                    literalVersion,
                    "closure contains a duplicate or conflicting GUID");
            }

            validated.Add(new ValidatedClosureItem(
                item.Ref!,
                item.Name!,
                item.Depth.Value,
                new Aevatar.AI.Abstractions.ExactRemoteSkillRef
                {
                    Guid = item.Guid!,
                    LiteralVersion = item.Version!,
                }));
        }
        return validated;
    }

    private static IReadOnlyList<Aevatar.AI.Abstractions.ExactRemoteSkillRef> ResolveDirectMembers(
        IReadOnlyList<OrnnSkillSetMember> members,
        IReadOnlyList<ValidatedClosureItem> closureItems,
        string guid,
        string literalVersion)
    {
        var roots = closureItems.Where(static item => item.Depth == 0).ToArray();
        if (roots.Length != members.Count)
        {
            throw ExactRemoteFetchException.IntegrityMismatch(
                ExactRemoteResourceKind.Skillset,
                guid,
                literalVersion,
                "closure root count differs from direct member count");
        }

        var usedRootGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<Aevatar.AI.Abstractions.ExactRemoteSkillRef>(members.Count);
        foreach (var member in members)
        {
            RequireText(member.Name, "direct member name", ExactRemoteResourceKind.Skillset, guid, literalVersion);
            RequireText(member.Version, "direct member version", ExactRemoteResourceKind.Skillset, guid, literalVersion);
            if (member.Guid is not null && !System.Guid.TryParse(member.Guid, out _))
            {
                throw ExactRemoteFetchException.InvalidResponse(
                    ExactRemoteResourceKind.Skillset,
                    guid,
                    literalVersion,
                    "direct member GUID was invalid");
            }
            var matches = roots.Where(root => MemberMatches(member, root)).ToArray();
            if (matches.Length != 1 || !usedRootGuids.Add(matches[0].Reference.Guid))
            {
                throw ExactRemoteFetchException.IntegrityMismatch(
                    ExactRemoteResourceKind.Skillset,
                    guid,
                    literalVersion,
                    "each direct member must resolve to one unique closure root");
            }
            resolved.Add(matches[0].Reference.Clone());
        }
        return resolved;
    }

    private static bool MemberMatches(OrnnSkillSetMember member, ValidatedClosureItem root)
    {
        if (!string.IsNullOrWhiteSpace(member.Guid) &&
            System.Guid.TryParse(member.Guid, out var memberGuid) &&
            System.Guid.TryParse(root.Reference.Guid, out var rootGuid))
        {
            return memberGuid == rootGuid;
        }

        if (!string.Equals(member.Name, root.Name, StringComparison.Ordinal))
            return false;
        return !IsLiteralVersion(member.Version) ||
               string.Equals(member.Version, root.Reference.LiteralVersion, StringComparison.Ordinal);
    }

    private static void RequireGuidMatch(
        string? actual,
        string expected,
        ExactRemoteResourceKind resourceKind,
        string literalVersion)
    {
        if (!System.Guid.TryParse(actual, out var actualGuid) ||
            !System.Guid.TryParse(expected, out var expectedGuid) ||
            actualGuid != expectedGuid)
        {
            throw ExactRemoteFetchException.IntegrityMismatch(
                resourceKind,
                expected,
                literalVersion,
                "returned GUID differs from the requested GUID");
        }
    }

    private static void RequireText(
        string? value,
        string field,
        ExactRemoteResourceKind resourceKind,
        string guid,
        string literalVersion)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw ExactRemoteFetchException.InvalidResponse(resourceKind, guid, literalVersion, $"{field} was missing or blank");
    }

    private static void RequireEqual(
        string? actual,
        string expected,
        string field,
        ExactRemoteResourceKind resourceKind,
        string guid,
        string literalVersion)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw ExactRemoteFetchException.IntegrityMismatch(resourceKind, guid, literalVersion, $"{field} mismatch");
    }

    private static void ValidateExactReference(
        string? guid,
        string? literalVersion,
        ExactRemoteResourceKind resourceKind)
    {
        if (!System.Guid.TryParseExact(guid, "D", out _) || !IsLiteralVersion(literalVersion))
        {
            throw ExactRemoteFetchException.InvalidResponse(
                resourceKind,
                guid ?? string.Empty,
                literalVersion ?? string.Empty,
                "reference must contain a D-format GUID and a literal major.minor version");
        }
    }

    private static bool IsLiteralVersion(string? version) =>
        version is not null && LiteralVersionPattern.IsMatch(version);

    private sealed record NyxIdProxyError(int Status, string Detail);

    private sealed record ValidatedClosureItem(
        string Ref,
        string Name,
        int Depth,
        Aevatar.AI.Abstractions.ExactRemoteSkillRef Reference);
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
    [JsonPropertyName("mcp-servers")]
    public List<OrnnMcpServerDeclaration>? McpServers { get; set; }
}

public sealed class OrnnMcpServerDeclaration
{
    public string? Mcp { get; set; }
    public string? Version { get; set; }
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
    /// <summary>Set-level master prompt authored on the skillset itself.</summary>
    public string? Instructions { get; set; }
    public bool IsPrivate { get; set; }
    public List<OrnnSkillSetMember> Members { get; set; } = [];
}

internal sealed record OrnnExactSkillEvidence(
    OrnnSkillJson Package,
    string PublishedName,
    ExactRemoteVersionProvenance Provenance,
    ExactRemotePackage ExactPackage,
    IReadOnlyList<ExactRemoteToolDeclaration> DeclaredTools);

internal sealed record OrnnExactSkillsetEvidence(
    string PublishedName,
    ExactRemoteVersionProvenance Provenance,
    string Instructions,
    IReadOnlyList<Aevatar.AI.Abstractions.ExactRemoteSkillRef> DirectMembers,
    IReadOnlyList<Aevatar.AI.Abstractions.ExactRemoteSkillRef> FullClosure);

internal sealed class OrnnExactSkillDetail
{
    public string? Guid { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? SkillHash { get; set; }
    public OrnnSkillMetadata? Metadata { get; set; }
}

internal sealed class OrnnExactVersionItems<T>
{
    public List<T> Items { get; set; } = [];
}

internal interface IOrnnExactVersionRow
{
    string? CreatedBy { get; }
    string? CreatedByEmail { get; }
    string? CreatedByDisplayName { get; }
    string? CreatedOn { get; }
}

internal sealed class OrnnExactSkillVersionRow : IOrnnExactVersionRow
{
    public string? Version { get; set; }
    public string? SkillHash { get; set; }
    public string? Integrity { get; set; }
    public string? CreatedBy { get; set; }
    public string? CreatedByEmail { get; set; }
    public string? CreatedByDisplayName { get; set; }
    public string? CreatedOn { get; set; }
}

internal sealed class OrnnExactSkillsetVersionRow : IOrnnExactVersionRow
{
    public string? Version { get; set; }
    public int? MemberCount { get; set; }
    public string? CreatedBy { get; set; }
    public string? CreatedByEmail { get; set; }
    public string? CreatedByDisplayName { get; set; }
    public string? CreatedOn { get; set; }
}

internal sealed class OrnnExactSkillsetClosure
{
    public string? Instructions { get; set; }
    public List<OrnnExactSkillsetClosureItem>? Items { get; set; }
}

internal sealed class OrnnExactSkillsetClosureItem
{
    public string? Ref { get; set; }
    public string? Guid { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public int? Depth { get; set; }
}

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
    internal string? RawReference { get; init; }

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
            ? new OrnnSkillSetMember
            {
                Name = trimmed[..at],
                Version = trimmed[(at + 1)..],
                RawReference = trimmed,
            }
            : new OrnnSkillSetMember { Name = trimmed, RawReference = trimmed };
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
