namespace Aevatar.GAgents.StatusDashboard.Configuration;

/// <summary>
/// Resolved view over <see cref="StatusDashboardOptions"/> — pre-validates the
/// manifest at startup so the GAgent module receives well-formed descriptors
/// instead of fishing for missing fields at runtime.
/// </summary>
public sealed class StatusDashboardManifest
{
    private const string HttpStatusProbe = "http_status";
    private const string ReadmodelFreshnessProbe = "readmodel_freshness";
    private const string AevatarCoreLoopProbe = "aevatar_core_loop";
    private const string AuditQueryIndexProbe = "audit_query_index";
    private const string NyxIdAuthorityPlaceholder = "${configuration:Aevatar:NyxId:Authority}";

    public StatusDashboardManifest(IReadOnlyList<HealthProbeTargetDescriptor> descriptors)
    {
        Descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
    }

    public IReadOnlyList<HealthProbeTargetDescriptor> Descriptors { get; }

    public static StatusDashboardManifest FromOptions(StatusDashboardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var defaultInterval = options.DefaultIntervalSeconds > 0
            ? options.DefaultIntervalSeconds
            : 60;
        var defaultTimeout = options.DefaultTimeoutMs > 0
            ? options.DefaultTimeoutMs
            : 5_000;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var descriptors = new List<HealthProbeTargetDescriptor>();
        var targets = ResolveTargets(options);
        foreach (var t in targets)
        {
            if (string.IsNullOrWhiteSpace(t.Slug)) continue;
            if (string.IsNullOrWhiteSpace(t.Probe)) continue;
            if (!seen.Add(t.Slug.Trim())) continue;

            var descriptor = new HealthProbeTargetDescriptor
            {
                Slug = t.Slug.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(t.Name) ? t.Slug.Trim() : t.Name.Trim(),
                Category = string.IsNullOrWhiteSpace(t.Category) ? "upstream" : t.Category.Trim().ToLowerInvariant(),
                Severity = NormalizeSeverity(t.Severity),
                ProbeKind = t.Probe.Trim(),
                IntervalSeconds = (t.IntervalSeconds.HasValue && t.IntervalSeconds.Value > 0)
                    ? t.IntervalSeconds.Value
                    : defaultInterval,
                TimeoutMs = (t.TimeoutMs.HasValue && t.TimeoutMs.Value > 0)
                    ? t.TimeoutMs.Value
                    : defaultTimeout,
                Enabled = t.Enabled,
            };
            foreach (var (k, v) in t.Parameters ?? new())
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                descriptor.Parameters[k.Trim()] = v ?? string.Empty;
            }
            descriptors.Add(descriptor);
        }

        return new StatusDashboardManifest(descriptors);
    }

    private static IReadOnlyList<StatusProbeTargetConfig> ResolveTargets(StatusDashboardOptions options)
    {
        var configuredTargets = options.Targets ?? new List<StatusProbeTargetConfig>();
        return configuredTargets.Count == 0 && options.UseBuiltInTargets
            ? BuiltInTargets(options)
            : configuredTargets;
    }

    private static string NormalizeSeverity(string? severity)
    {
        var normalized = severity?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "critical" or "standard" or "canary" => normalized,
            _ => "standard",
        };
    }

    private static string NormalizeSelfBaseUrl(string? selfBaseUrl) =>
        string.IsNullOrWhiteSpace(selfBaseUrl)
            ? "http://localhost:8080"
            : selfBaseUrl.Trim().TrimEnd('/');

    private static List<StatusProbeTargetConfig> BuiltInTargets(StatusDashboardOptions options)
    {
        var selfBaseUrl = NormalizeSelfBaseUrl(options.SelfBaseUrl);
        var probe = options.Probe ?? new StatusProbeOptions();
        var targets = new List<StatusProbeTargetConfig>
        {
            // ── self ──
            HttpTarget(
                slug: "self-liveness",
                name: "HTTP API · liveness",
                category: "self",
                severity: "standard",
                url: $"{selfBaseUrl}/health/live"),
            HttpTarget(
                slug: "self-readiness",
                name: "HTTP API · readiness",
                category: "self",
                severity: "critical",
                url: $"{selfBaseUrl}/health/ready",
                parameters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["DegradedOnNon2xx"] = "true",
                }),

            // ── studio / app (anonymous health surfaces) ──
            // These endpoints return 200 to anonymous callers, so a 200 here is a real
            // success signal (canon §9: never use an "expect 401" auth gate as a health check).
            HttpTarget(
                slug: "studio-health",
                name: "Studio · /api/health",
                category: "studio",
                severity: "standard",
                url: $"{selfBaseUrl}/api/health"),
            HttpTarget(
                slug: "app-context",
                name: "App · /api/app/context",
                category: "studio",
                severity: "standard",
                url: $"{selfBaseUrl}/api/app/context"),

            // ── core features (in-process) ──
            new()
            {
                Slug = "aevatar-core-loop-tools",
                Name = "Aevatar Core Loop Tools",
                Category = "feature",
                Severity = "critical",
                Probe = AevatarCoreLoopProbe,
                IntervalSeconds = 60,
                Parameters =
                {
                    ["ToolSet"] = "workspace.default",
                    ["RequireWorkspaceSources"] = "true",
                },
            },
            new()
            {
                Slug = "channel-bot-runtime",
                Name = "Channel Bot Registrations",
                Category = "feature",
                Severity = "standard",
                Probe = ReadmodelFreshnessProbe,
                IntervalSeconds = 60,
                Parameters =
                {
                    ["Source"] = "channel-bot-registrations",
                    ["MinCount"] = "0",
                },
            },
            new()
            {
                Slug = "audit-query-index",
                Name = "Audit Trail Query / Index",
                Category = "feature",
                Severity = "standard",
                Probe = AuditQueryIndexProbe,
                IntervalSeconds = 60,
            },

            // ── upstream ──
            HttpTarget(
                slug: "nyxid-http-health",
                name: "NyxID · health",
                category: "upstream",
                severity: "standard",
                url: $"{NyxIdAuthorityPlaceholder}/health",
                intervalSeconds: 60),
            HttpTarget(
                slug: "nyxid-oidc-discovery",
                name: "NyxID · OIDC discovery",
                category: "upstream",
                severity: "standard",
                url: $"{NyxIdAuthorityPlaceholder}/.well-known/openid-configuration",
                intervalSeconds: 60),
        };

        // ── paid LLM canary (real end-to-end completion) ──
        // Emitted only when a real NyxID-recognized credential is configured. Reuses the
        // static_bearer auth mode — the executor reads the secret from the configuration key, so
        // no new auth code is required. Absent the credential the canary is simply not probed
        // (no false "down"); the canary severity means a failure degrades but never blacks out.
        if (!string.IsNullOrWhiteSpace(probe.CanaryBearer))
        {
            // Credentialed catalog read: 200 + a valid list response proves the LLM ingress
            // and NyxID catalog aggregation work end-to-end, with no model invocation cost.
            targets.Add(HttpTarget(
                slug: "llm-catalog",
                name: "LLM ingress · model catalog",
                category: "llm",
                severity: "canary",
                url: $"{selfBaseUrl}/v1/models",
                expectedStatuses: "200",
                parameters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ExpectedBodyContains"] = "\"object\"",
                    ["Auth.Mode"] = "static_bearer",
                    ["Auth.StaticBearerConfigurationKey"] = StatusProbeOptions.CanaryBearerConfigurationKey,
                }));

            var model = string.IsNullOrWhiteSpace(probe.CanaryModel)
                ? StatusProbeOptions.DefaultCanaryModel
                : probe.CanaryModel.Trim();
            var maxTokens = probe.CanaryMaxTokens > 0 ? probe.CanaryMaxTokens : 8;
            var canaryInterval = probe.CanaryIntervalSeconds > 0 ? probe.CanaryIntervalSeconds : 900;
            var body =
                $$"""{"model":"{{model}}","messages":[{"role":"user","content":"ping"}],"max_tokens":{{maxTokens}},"temperature":0}""";

            targets.Add(HttpTarget(
                slug: "llm-completion-canary",
                name: "LLM canary · chat completion",
                category: "llm",
                severity: "canary",
                url: $"{selfBaseUrl}/v1/chat/completions",
                method: "POST",
                expectedStatuses: "200",
                intervalSeconds: canaryInterval,
                parameters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Body"] = body,
                    ["ContentType"] = "application/json",
                    ["ExpectedBodyContains"] = "choices",
                    ["Auth.Mode"] = "static_bearer",
                    ["Auth.StaticBearerConfigurationKey"] = StatusProbeOptions.CanaryBearerConfigurationKey,
                }));
        }

        // ── credentialed orchestration / observatory reads ──
        // Emitted when a probe scope is configured. Use a self-issued scope service token and assert
        // real success (200) — never a 401 auth gate (canon §9.1). When scope service tokens are not
        // enabled the executor degrades these to "unknown", never a false "down".
        if (!string.IsNullOrWhiteSpace(probe.ScopeId))
        {
            var scopeId = probe.ScopeId.Trim();
            targets.Add(HttpTarget(
                slug: "orchestration-scope-read",
                name: "Orchestration · scope services",
                category: "orchestration",
                severity: "standard",
                url: $"{selfBaseUrl}/api/scopes/{scopeId}/services",
                parameters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Auth.Mode"] = "scope_service_token",
                    ["Auth.ScopeId"] = scopeId,
                }));
            targets.Add(HttpTarget(
                slug: "observatory-read",
                name: "Observatory · caller identity",
                category: "orchestration",
                severity: "standard",
                url: $"{selfBaseUrl}/api/workflow/observatory/me",
                parameters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Auth.Mode"] = "scope_service_token",
                    ["Auth.ScopeId"] = scopeId,
                }));
        }

        return targets;
    }

    private static StatusProbeTargetConfig HttpTarget(
        string slug,
        string name,
        string category,
        string url,
        string severity = "standard",
        string method = "GET",
        string expectedStatuses = "200",
        int intervalSeconds = 60,
        int? timeoutMs = null,
        Dictionary<string, string>? parameters = null)
    {
        var target = new StatusProbeTargetConfig
        {
            Slug = slug,
            Name = name,
            Category = category,
            Severity = severity,
            Probe = HttpStatusProbe,
            IntervalSeconds = intervalSeconds,
            TimeoutMs = timeoutMs,
        };
        target.Parameters["Url"] = url;
        target.Parameters["Method"] = method;
        target.Parameters["ExpectedStatuses"] = expectedStatuses;
        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
                target.Parameters[key] = value;
        }
        return target;
    }

}
