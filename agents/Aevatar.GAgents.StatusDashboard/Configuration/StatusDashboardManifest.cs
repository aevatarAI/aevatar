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
            ? BuiltInTargets(NormalizeSelfBaseUrl(options.SelfBaseUrl))
            : configuredTargets;
    }

    private static string NormalizeSelfBaseUrl(string? selfBaseUrl) =>
        string.IsNullOrWhiteSpace(selfBaseUrl)
            ? "http://localhost:8080"
            : selfBaseUrl.Trim().TrimEnd('/');

    private static List<StatusProbeTargetConfig> BuiltInTargets(string selfBaseUrl) =>
    [
        HttpTarget(
            slug: "self-liveness",
            name: "HTTP API (liveness)",
            category: "self",
            url: $"{selfBaseUrl}/health/live"),
        HttpTarget(
            slug: "self-readiness",
            name: "HTTP API (readiness)",
            category: "self",
            url: $"{selfBaseUrl}/health/ready",
            parameters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ExpectedStatuses"] = "200",
                ["DegradedOnNon2xx"] = "true",
            }),
        HttpTarget(
            slug: "responses-api-auth-gate",
            name: "Responses API auth gate",
            category: "feature",
            url: $"{selfBaseUrl}/v1/responses",
            method: "POST",
            expectedStatuses: "401",
            intervalSeconds: 60,
            parameters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Body"] = "{}",
            }),
        HttpTarget(
            slug: "messages-api-auth-gate",
            name: "Anthropic Messages API auth gate",
            category: "feature",
            url: $"{selfBaseUrl}/v1/messages",
            method: "POST",
            expectedStatuses: "401",
            intervalSeconds: 60,
            parameters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Body"] = "{}",
            }),
        HttpTarget(
            slug: "models-api-auth-gate",
            name: "Models API auth gate",
            category: "feature",
            url: $"{selfBaseUrl}/v1/models",
            expectedStatuses: "401",
            intervalSeconds: 60),
        HttpTarget(
            slug: "voice-websocket-auth-gate",
            name: "Voice WebSocket route",
            category: "feature",
            url: $"{selfBaseUrl}/ws/voice",
            expectedStatuses: "400,401",
            intervalSeconds: 60),
        HttpTarget(
            slug: "channel-registration-api-auth-gate",
            name: "Channel registration API auth gate",
            category: "feature",
            url: $"{selfBaseUrl}/api/channels/registrations",
            expectedStatuses: "200,401",
            intervalSeconds: 60),
        new()
        {
            Slug = "channel-bot-runtime",
            Name = "Channel Bot Registrations",
            Category = "feature",
            Probe = ReadmodelFreshnessProbe,
            IntervalSeconds = 60,
            Parameters =
            {
                ["Source"] = "channel-bot-registrations",
                ["MinCount"] = "0",
            },
        },
        HttpTarget(
            slug: "nyxid-llm-status",
            name: "NyxID LLM status",
            category: "upstream",
            url: $"{NyxIdAuthorityPlaceholder}/api/v1/llm/status",
            expectedStatuses: "200,401",
            intervalSeconds: 60),
        HttpTarget(
            slug: "nyxid-llm-gateway-auth-gate",
            name: "NyxID LLM gateway auth gate",
            category: "upstream",
            url: $"{NyxIdAuthorityPlaceholder}/api/v1/llm/gateway/v1/models",
            expectedStatuses: "200,401",
            intervalSeconds: 60),
        HttpTarget(
            slug: "nyxid-channel-bots-auth-gate",
            name: "NyxID channel bots auth gate",
            category: "upstream",
            url: $"{NyxIdAuthorityPlaceholder}/api/v1/channel-bots",
            expectedStatuses: "200,401",
            intervalSeconds: 60),
        HttpTarget(
            slug: "nyxid-channel-relay-reply-auth-gate",
            name: "NyxID channel relay reply auth gate",
            category: "upstream",
            url: $"{NyxIdAuthorityPlaceholder}/api/v1/channel-relay/reply",
            method: "POST",
            expectedStatuses: "200,400,401,422",
            intervalSeconds: 60,
            parameters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Body"] = "{}",
            }),
    ];

    private static StatusProbeTargetConfig HttpTarget(
        string slug,
        string name,
        string category,
        string url,
        string method = "GET",
        string expectedStatuses = "200",
        int intervalSeconds = 60,
        Dictionary<string, string>? parameters = null)
    {
        var target = new StatusProbeTargetConfig
        {
            Slug = slug,
            Name = name,
            Category = category,
            Probe = HttpStatusProbe,
            IntervalSeconds = intervalSeconds,
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
