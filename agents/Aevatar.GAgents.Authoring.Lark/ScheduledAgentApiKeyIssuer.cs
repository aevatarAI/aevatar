using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Authoring.Lark;

internal sealed class ScheduledAgentApiKeyIssuer : IScheduledAgentApiKeyIssuer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Omit null fields (notably target_org_id) so an all-personal key request stays byte-identical
    // to the pre-org behavior; only org-owned scoped keys carry target_org_id.
    private static readonly JsonSerializerOptions CreateKeyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly INyxIdApiClientFactory _nyxClientFactory;
    private readonly ScheduledAgentCreatorOptions _options;
    private readonly IOwnerLlmConfigSource? _ownerLlmConfigSource;
    private readonly ILogger<ScheduledAgentApiKeyIssuer>? _logger;

    public ScheduledAgentApiKeyIssuer(
        INyxIdApiClientFactory nyxClientFactory,
        ScheduledAgentCreatorOptions options,
        IOwnerLlmConfigSource? ownerLlmConfigSource = null,
        ILogger<ScheduledAgentApiKeyIssuer>? logger = null)
    {
        _nyxClientFactory = nyxClientFactory ?? throw new ArgumentNullException(nameof(nyxClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ownerLlmConfigSource = ownerLlmConfigSource;
        _logger = logger;
    }

    public async Task<ScheduledAgentApiKeyIssueResult> IssueAsync(
        string token,
        ScheduledAgentServiceSlugs serviceSlugs,
        string agentId,
        string skillName,
        string? scopeId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(serviceSlugs);

        var ownerLlmRouteSlug = await ResolveOwnerLlmRouteServiceSlugAsync(scopeId, ct);
        var slugs = RequiredSlugs(serviceSlugs, ownerLlmRouteSlug).Distinct(StringComparer.Ordinal).ToArray();
        if (slugs.Length == 0)
            return ScheduledAgentApiKeyIssueResult.Failed("missing_required_service_slugs");

        var client = _nyxClientFactory.CreateClient();
        var servicesJson = await client.ListServicesAsync(token, ct);
        var resolution = ResolveServiceIds(servicesJson, slugs);
        if (resolution.Error is not null)
            return ScheduledAgentApiKeyIssueResult.Failed(resolution.Error, resolution.Detail, resolution.Hint);

        if (!TryResolveKeyExpiresAt(out var keyExpiresAtUtc, out var expiryError))
            return ScheduledAgentApiKeyIssueResult.Failed(expiryError);

        var response = await client.CreateApiKeyAsync(
            token,
            JsonSerializer.Serialize(new
            {
                name = $"aevatar-scheduled-agent-{agentId}",
                scopes = "read proxy",
                platform = "generic",
                allow_all_services = false,
                allow_all_nodes = false,
                allowed_service_ids = resolution.ServiceIds,
                // When every required service is shared through one organization, the scoped key
                // must be created under that org (its user_id) so NyxID's per-owner allowed_service_id
                // check passes. Null for all-personal runs, and omitted from the payload by
                // CreateKeyJsonOptions.
                target_org_id = resolution.TargetOrgId,
                expires_at = keyExpiresAtUtc.ToString("O"),
            }, CreateKeyJsonOptions),
            ct);

        var issuedKey = ExtractIssuedKey(response, keyExpiresAtUtc.ToUnixTimeMilliseconds());
        if (!issuedKey.Success)
            return issuedKey;

        if (string.IsNullOrWhiteSpace(skillName))
            return issuedKey;

        var ornnSlug = GetOrnnServiceSlug();
        var preflight = await issuedKey.Secret!.Use(fullKey =>
            PreflightSkillFetchAsync(client, fullKey, ornnSlug, skillName, ct));
        if (preflight.Success)
            return issuedKey;

        return ScheduledAgentApiKeyIssueResult.FailedAfterIssue(
            issuedKey.ApiKeyId ?? string.Empty,
            preflight.Error ?? "scheduled_skill_preflight_failed",
            preflight.Detail,
            preflight.Hint,
            preflight.HttpStatus,
            ornnSlug,
            skillName.Trim());
    }

    public async Task<ScheduledAgentApiKeyRevokeResult> RevokeAsync(string token, string apiKeyId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return ScheduledAgentApiKeyRevokeResult.Pending(
                0,
                "missing_access_token",
                UserAgentApiKeyRevocationFailureKind.Unauthorized);
        }

        if (string.IsNullOrWhiteSpace(apiKeyId))
        {
            return ScheduledAgentApiKeyRevokeResult.Pending(
                0,
                "missing_api_key_id",
                UserAgentApiKeyRevocationFailureKind.ProviderError);
        }

        var response = await _nyxClientFactory.CreateClient().DeleteApiKeyAsync(token, apiKeyId.Trim(), ct);
        if (!TryReadErrorEnvelope(response, out var status, out var body, out var message))
            return ScheduledAgentApiKeyRevokeResult.Complete();

        if (status == 404)
            return ScheduledAgentApiKeyRevokeResult.Complete(404);

        var detail = Normalize(body) ?? Normalize(message) ?? "nyxid_api_key_revoke_failed";
        return ScheduledAgentApiKeyRevokeResult.Pending(
            status ?? 0,
            detail,
            ClassifyRevocationFailure(status));
    }

    private static UserAgentApiKeyRevocationFailureKind ClassifyRevocationFailure(int? status) =>
        status switch
        {
            401 or 403 => UserAgentApiKeyRevocationFailureKind.Unauthorized,
            429 => UserAgentApiKeyRevocationFailureKind.Transient,
            >= 500 => UserAgentApiKeyRevocationFailureKind.Transient,
            _ => UserAgentApiKeyRevocationFailureKind.ProviderError,
        };

    private IEnumerable<string> RequiredSlugs(ScheduledAgentServiceSlugs serviceSlugs, string? ownerLlmRouteSlug)
    {
        var ornnSlug = GetOrnnServiceSlug();
        if (serviceSlugs.RequiresOrnnService)
            yield return ornnSlug;
        yield return serviceSlugs.PrimaryOutboundSlug;

        if (!string.IsNullOrWhiteSpace(serviceSlugs.FailureNotificationSlug) &&
            !string.Equals(serviceSlugs.FailureNotificationSlug, serviceSlugs.PrimaryOutboundSlug, StringComparison.Ordinal))
        {
            yield return serviceSlugs.FailureNotificationSlug;
        }

        foreach (var slug in serviceSlugs.RequiredServiceSlugs)
        {
            if (!string.IsNullOrWhiteSpace(slug))
                yield return slug;
        }

        // The scheduled run pins the bot owner's pre-configured NyxID LLM route
        // (OwnerLlmConfigApplier, mirrored by SkillRunnerGAgent/WorkflowAgentGAgent). When that
        // route is a custom proxy service (e.g. `/api/v1/proxy/s/chrono-llm`) rather than the
        // shared gateway, the scoped key must be authorized for that UserService — otherwise
        // NyxID's proxy scope check rejects every run with HTTP 403 api_key_scope_forbidden
        // (error_code 9000) even though the schedule itself fired correctly.
        if (!string.IsNullOrWhiteSpace(ownerLlmRouteSlug))
            yield return ownerLlmRouteSlug;
    }

    private async Task<string?> ResolveOwnerLlmRouteServiceSlugAsync(string? scopeId, CancellationToken ct)
    {
        if (_ownerLlmConfigSource is null || string.IsNullOrWhiteSpace(scopeId))
            return null;

        OwnerLlmConfig config;
        try
        {
            config = await _ownerLlmConfigSource.GetForScopeAsync(scopeId.Trim(), ct) ?? OwnerLlmConfig.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Mirror OwnerLlmConfigApplier's swallow-and-log policy: a flaky user-config
            // projection must not fail agent creation. The key is minted without the LLM-route
            // grant (identical to pre-fix behavior) so the misconfiguration, if any, surfaces at
            // run time rather than blocking creation on a transient lookup error.
            _logger?.LogWarning(
                ex,
                "Scheduled agent key issuance could not load owner LLM config for scope {ScopeId}; minting key without an LLM-route grant",
                scopeId);
            return null;
        }

        return ExtractProxyServiceSlug(config.PreferredLlmRoute);
    }

    /// <summary>
    /// Maps a saved owner LLM route preference to the NyxID proxy <c>service</c> slug that the
    /// scoped key must be authorized for, or <c>null</c> when no per-service grant is needed.
    /// Returns a slug only for proxy-service routes (<c>/api/v1/proxy/s/{slug}</c>) or a bare
    /// slug; the shared gateway route and absolute non-proxy paths use the bearer token directly
    /// and resolve to <c>null</c>. Mirrors <c>NyxIdLlmServiceCatalogParser.NormalizeProxyRouteValue</c>.
    /// </summary>
    internal static string? ExtractProxyServiceSlug(string? preferredLlmRoute)
    {
        var normalized = preferredLlmRoute?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        // A full URL / scheme-relative value cannot be mapped to a UserService id here; the
        // provider treats such values as "use the gateway" anyway, which needs no per-service grant.
        if (normalized.Contains("://", StringComparison.Ordinal) ||
            normalized.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        const string proxyPrefix = "/api/v1/proxy/s/";
        var prefixIndex = normalized.IndexOf(proxyPrefix, StringComparison.OrdinalIgnoreCase);
        if (prefixIndex >= 0)
            return FirstPathSegment(normalized[(prefixIndex + proxyPrefix.Length)..]);

        // Any other absolute path (e.g. the `/api/v1/llm/gateway/v1` gateway route) is not a
        // per-service proxy route, so there is nothing to authorize.
        if (normalized.StartsWith("/", StringComparison.Ordinal))
            return null;

        // Bare slug form.
        return FirstPathSegment(normalized);
    }

    private static string? FirstPathSegment(string value)
    {
        var segment = value.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(segment))
            return null;

        var end = segment.IndexOfAny(['/', '?', '#']);
        if (end >= 0)
            segment = segment[..end];

        segment = segment.Trim();
        return string.IsNullOrWhiteSpace(segment) ? null : Uri.UnescapeDataString(segment);
    }

    private async Task<ScheduledAgentSkillPreflightResult> PreflightSkillFetchAsync(
        NyxIdApiClient client,
        string apiKey,
        string ornnSlug,
        string skillName,
        CancellationToken ct)
    {
        var normalizedSkillName = skillName.Trim();
        var response = await client.ProxyRequestAsync(
            apiKey,
            ornnSlug,
            $"/api/v1/skills/{Uri.EscapeDataString(normalizedSkillName)}/json",
            "GET",
            null,
            null,
            ct);

        if (!TryReadErrorEnvelope(response, out var status, out var body, out var message))
            return ScheduledAgentSkillPreflightResult.Succeeded();

        var detailSuffix = string.IsNullOrWhiteSpace(body)
            ? message
            : body;
        var detail = status switch
        {
            403 => $"NyxID proxy returned 403 while fetching scheduled skill '{normalizedSkillName}' with the newly issued agent key. " +
                   $"The key is missing proxy scope or service authorization for service '{ornnSlug}'." +
                   (string.IsNullOrWhiteSpace(detailSuffix) ? string.Empty : $" Response: {detailSuffix}"),
            404 => $"NyxID proxy returned 404 while fetching scheduled skill '{normalizedSkillName}' through service '{ornnSlug}'." +
                   (string.IsNullOrWhiteSpace(detailSuffix) ? string.Empty : $" Response: {detailSuffix}"),
            _ => $"NyxID proxy preflight failed while fetching scheduled skill '{normalizedSkillName}' through service '{ornnSlug}'" +
                 (status.HasValue ? $" with status {status.Value}" : string.Empty) +
                 (string.IsNullOrWhiteSpace(detailSuffix) ? "." : $". Response: {detailSuffix}"),
        };

        return status switch
        {
            403 => ScheduledAgentSkillPreflightResult.Failed(
                "scheduled_skill_preflight_access_denied",
                detail,
                "Connect the Ornn service in NyxID and recreate the scheduled agent so its scoped key includes proxy access to that UserService.",
                status),
            404 => ScheduledAgentSkillPreflightResult.Failed(
                "scheduled_skill_preflight_skill_not_found",
                detail,
                "Check skill_ref and ensure the referenced Ornn skill is visible to the scheduled agent credential.",
                status),
            _ => ScheduledAgentSkillPreflightResult.Failed(
                "scheduled_skill_preflight_failed",
                detail,
                "Fix the NyxID Ornn service binding or skill reference, then retry scheduled agent creation.",
                status),
        };
    }

    private static ScheduledAgentServiceResolution ResolveServiceIds(string json, IReadOnlyCollection<string> requiredSlugs)
    {
        if (LooksLikeErrorEnvelope(json))
            return ScheduledAgentServiceResolution.Failed("service_resolution_failed");

        var matches = requiredSlugs
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(static slug => slug, static _ => new List<ResolvedService>(), StringComparer.Ordinal);

        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var item in EnumerateServiceItems(document.RootElement))
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var slug = ReadString(item, "slug") ?? ReadString(item, "service_slug");
                if (slug is null || !matches.TryGetValue(slug, out var candidates))
                    continue;

                var id = ReadString(item, "id") ?? ReadString(item, "service_id") ?? ReadString(item, "user_service_id");
                if (!string.IsNullOrWhiteSpace(id))
                    candidates.Add(new ResolvedService(id.Trim(), ReadServiceOwner(item)));
            }
        }
        catch (JsonException)
        {
            return ScheduledAgentServiceResolution.Failed("service_resolution_invalid_json");
        }

        var resolvedIds = new List<string>();
        var distinctOwners = new Dictionary<string, ServiceOwner>(StringComparer.Ordinal);
        var ownerBySlug = new List<(string Slug, ServiceOwner Owner)>();
        foreach (var slug in requiredSlugs.Distinct(StringComparer.Ordinal))
        {
            var byId = matches[slug]
                .GroupBy(static candidate => candidate.Id, StringComparer.Ordinal)
                .ToArray();
            if (byId.Length == 0)
                return ScheduledAgentServiceResolution.Failed($"required_service_not_found:{slug}");
            if (byId.Length > 1)
                return ScheduledAgentServiceResolution.Failed($"required_service_ambiguous:{slug}");

            var resolved = byId[0].First();
            resolvedIds.Add(resolved.Id);
            distinctOwners[resolved.Owner.Key] = resolved.Owner;
            ownerBySlug.Add((slug, resolved.Owner));
        }

        if (resolvedIds.Count == 0)
            return ScheduledAgentServiceResolution.Failed("required_service_ids_empty");

        // A NyxID API key has a single owner and every allowed_service_id is validated against it.
        // Scheduled invocation keys must stay least-privilege, so cross-owner service sets fail
        // closed instead of falling back to allow_all_services.
        if (distinctOwners.Count > 1)
        {
            var unreachable = ownerBySlug
                .Where(static entry => !entry.Owner.Reachable)
                .Select(static entry => entry.Slug)
                .ToArray();
            if (unreachable.Length > 0)
            {
                return ScheduledAgentServiceResolution.Failed(
                    "required_service_unreachable",
                    $"The scheduled agent requires org services the caller cannot proxy (view-only access): {string.Join(", ", unreachable)}.",
                    "Ask an organization admin to grant you member (not viewer) access to these services, then recreate the scheduled agent.");
            }

            return ScheduledAgentServiceResolution.Failed(
                "required_services_cross_owner",
                "The scheduled agent requires services owned by different NyxID owners, which cannot be represented as one least-privilege agent key.",
                "Move the required services under one owner or create separate scheduled agents.");
        }

        // Single owner: keep the tight allowlist (least privilege). Null target_org_id for all-personal
        // services; the org's user_id when every service is shared through one org.
        return new ScheduledAgentServiceResolution(resolvedIds, distinctOwners.Values.Single().OrgId, null);
    }

    private static ServiceOwner ReadServiceOwner(JsonElement item)
    {
        if (!item.TryGetProperty("credential_source", out var source) || source.ValueKind != JsonValueKind.Object)
            return ServiceOwner.Personal;

        // NyxID serializes provenance as
        // { "type": "personal" | "org", "org_id": ..., "org_name": ..., "allowed": true|false }.
        if (!string.Equals(ReadString(source, "type"), "org", StringComparison.OrdinalIgnoreCase))
            return ServiceOwner.Personal;

        var orgId = ReadString(source, "org_id");
        // An org provenance without a usable org id cannot be targeted; treat it as personal so the
        // ownership check fails loudly at NyxID rather than silently dropping the field.
        if (string.IsNullOrWhiteSpace(orgId))
            return ServiceOwner.Personal;

        // allowed=false marks a viewer membership NyxID will not proxy; absent or true means reachable.
        var reachable = !(source.TryGetProperty("allowed", out var allowed) && allowed.ValueKind == JsonValueKind.False);
        return ServiceOwner.ForOrg(orgId, ReadString(source, "org_name"), reachable);
    }

    private static ScheduledAgentApiKeyIssueResult ExtractIssuedKey(string response, long keyExpiresAtUnixMs)
    {
        // NyxID's 400 reason (e.g. "UserService '<id>' not found or not owned by user") is carried
        // in the adapter error envelope's status/body. Surface it instead of collapsing every
        // create failure to an opaque code the chat/runner cannot act on.
        if (TryReadErrorEnvelope(response, out var status, out var body, out var message))
        {
            var detailSuffix = string.IsNullOrWhiteSpace(body) ? message : body;
            var detail = "NyxID rejected the scheduled-agent API key creation" +
                         (status.HasValue ? $" with HTTP {status.Value}" : string.Empty) +
                         (string.IsNullOrWhiteSpace(detailSuffix) ? "." : $". Response: {detailSuffix}");
            var hint = status switch
            {
                400 => "A required NyxID service is not owned by the key's account. If the services are shared through an organization, ensure they all belong to the same org and that you are an admin of it; if they are personal, ensure none were disabled.",
                403 => "The caller cannot create an API key under the resolved owner. For org-owned services you must be an admin of that organization.",
                _ => "Inspect the NyxID response detail, fix the offending service, then recreate the scheduled agent.",
            };
            return ScheduledAgentApiKeyIssueResult.Failed("api_key_create_failed", detail, hint, status);
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var id = ReadString(root, "id");
            var fullKey = ReadString(root, "full_key");
            if (string.IsNullOrWhiteSpace(id))
                return ScheduledAgentApiKeyIssueResult.Failed("api_key_create_missing_id");
            if (string.IsNullOrWhiteSpace(fullKey))
                return ScheduledAgentApiKeyIssueResult.Failed("api_key_create_missing_full_key");

            return ScheduledAgentApiKeyIssueResult.Succeeded(id.Trim(), fullKey.Trim(), keyExpiresAtUnixMs);
        }
        catch (JsonException)
        {
            return ScheduledAgentApiKeyIssueResult.Failed("api_key_create_invalid_json");
        }
    }

    private static IEnumerable<JsonElement> EnumerateServiceItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var propertyName in new[] { "services", "user_services", "keys", "data" })
        {
            if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in array.EnumerateArray())
                yield return item;
        }
    }

    private static bool LooksLikeErrorEnvelope(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return true;

        try
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind is not (JsonValueKind.False or JsonValueKind.Null);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadErrorEnvelope(
        string? response,
        out int? status,
        out string? body,
        out string? message)
    {
        status = null;
        body = null;
        message = null;

        if (string.IsNullOrWhiteSpace(response))
        {
            message = "empty_response";
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("error", out var error) ||
                error.ValueKind is JsonValueKind.False or JsonValueKind.Null)
            {
                return false;
            }

            status = root.TryGetProperty("status", out var statusValue) &&
                     statusValue.ValueKind == JsonValueKind.Number &&
                     statusValue.TryGetInt32(out var parsedStatus)
                ? parsedStatus
                : null;
            body = ReadString(root, "body");
            message = ReadString(root, "message");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Normalize(value.GetString())
            : null;

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private string GetOrnnServiceSlug() =>
        Normalize(_options.OrnnServiceSlug) ?? ScheduledAgentCreatorOptions.DefaultOrnnServiceSlug;

    private bool TryResolveKeyExpiresAt(out DateTimeOffset expiresAtUtc, out string error)
    {
        expiresAtUtc = default;
        error = string.Empty;
        if (_options.ApiKeyLifetimeDays <= 0)
        {
            error = "api_key_lifetime_invalid";
            return false;
        }

        expiresAtUtc = DateTimeOffset.UtcNow.AddDays(_options.ApiKeyLifetimeDays);
        return true;
    }

    private sealed record ScheduledAgentServiceResolution(
        IReadOnlyList<string> ServiceIds,
        string? TargetOrgId,
        string? Error,
        string? Detail = null,
        string? Hint = null)
    {
        public static ScheduledAgentServiceResolution Failed(string error, string? detail = null, string? hint = null) =>
            new([], null, error, detail, hint);
    }

    private sealed record ResolvedService(string Id, ServiceOwner Owner);

    /// <summary>
    /// Owner of a resolved NyxID service: personal (<see cref="OrgId"/> is <c>null</c>) or an
    /// organization identified by its user id. Used to decide the <c>target_org_id</c> the scoped
    /// key must be created under so NyxID's per-owner <c>allowed_service_ids</c> check passes.
    /// <see cref="Reachable"/> is <c>false</c> for an org service the caller only views
    /// (<c>allowed=false</c>) and therefore cannot proxy.
    /// </summary>
    private sealed record ServiceOwner(string? OrgId, string? OrgName, bool Reachable)
    {
        public static readonly ServiceOwner Personal = new((string?)null, null, true);

        public static ServiceOwner ForOrg(string orgId, string? orgName, bool reachable) =>
            new(orgId, orgName, reachable);

        public string Key => OrgId is null ? "personal" : $"org:{OrgId}";

        public string DisplayName => OrgId is null
            ? "personal"
            : string.IsNullOrWhiteSpace(OrgName) ? $"org {OrgId}" : $"org {OrgName}";
    }
}

internal sealed record ScheduledAgentSkillPreflightResult(
    bool Success,
    string? Error,
    string? Detail,
    string? Hint,
    int? HttpStatus)
{
    public static ScheduledAgentSkillPreflightResult Succeeded() =>
        new(true, null, null, null, null);

    public static ScheduledAgentSkillPreflightResult Failed(
        string error,
        string detail,
        string hint,
        int? httpStatus) =>
        new(false, error, detail, hint, httpStatus);
}
