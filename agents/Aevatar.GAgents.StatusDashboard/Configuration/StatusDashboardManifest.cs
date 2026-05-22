using System.Text.Json;
using System.Text.RegularExpressions;

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
    private const string ResponsesForwardTeamInternalProbe = "responses_forward_team_internal";
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
            ? BuiltInTargets(options)
            : configuredTargets;
    }

    private static string NormalizeSelfBaseUrl(string? selfBaseUrl) =>
        string.IsNullOrWhiteSpace(selfBaseUrl)
            ? "http://localhost:8080"
            : selfBaseUrl.Trim().TrimEnd('/');

    private static List<StatusProbeTargetConfig> BuiltInTargets(StatusDashboardOptions options)
    {
        var selfBaseUrl = NormalizeSelfBaseUrl(options.SelfBaseUrl);
        var targets = new List<StatusProbeTargetConfig>
        {
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
        };

        targets.AddRange(ResponsesForwardToTeamTargets(selfBaseUrl, options.ResponsesForwardToTeam));
        return targets;
    }

    private static StatusProbeTargetConfig ResponsesForwardToTeamIdentityTarget(
        string nyxBaseUrl,
        string category,
        int intervalSeconds,
        int timeoutMs,
        IReadOnlyDictionary<string, string> auth)
    {
        return HttpTarget(
            slug: "responses-forward-team-00-nyxid-identity",
            name: "Responses -> Studio Team 00 NyxID identity",
            category: category,
            url: $"{nyxBaseUrl}/api/v1/users/me",
            expectedStatuses: "200",
            intervalSeconds: intervalSeconds,
            timeoutMs: timeoutMs,
            parameters: AuthenticatedParameters(auth,
                expectedBodyRegex: """(?s)"(id|user_id|sub)"\s*:"""));
    }

    private static IReadOnlyList<StatusProbeTargetConfig> ResponsesForwardToTeamTargets(
        string selfBaseUrl,
        ResponsesForwardToTeamStatusProbeOptions options)
    {
        if (!options.Enabled)
            return [];

        var directBaseUrl = NormalizeSelfBaseUrl(
            string.IsNullOrWhiteSpace(options.DirectBaseUrl) ? selfBaseUrl : options.DirectBaseUrl);
        var nyxBaseUrl = NormalizeSelfBaseUrl(
            string.IsNullOrWhiteSpace(options.NyxIdBaseUrl) ? NyxIdAuthorityPlaceholder : options.NyxIdBaseUrl);
        var serviceSlug = string.IsNullOrWhiteSpace(options.NyxIdServiceSlug)
            ? "aevatar"
            : options.NyxIdServiceSlug.Trim();
        var endpointId = string.IsNullOrWhiteSpace(options.EndpointId) ? "chat" : options.EndpointId.Trim();
        var model = string.IsNullOrWhiteSpace(options.Model) ? "deepseek/deepseek-chat" : options.Model.Trim();
        var prompt = string.IsNullOrWhiteSpace(options.Prompt)
            ? "Aevatar status probe. Reply in one short sentence."
            : options.Prompt.Trim();
        var intervalSeconds = options.IntervalSeconds > 0 ? options.IntervalSeconds : 300;
        var timeoutMs = options.TimeoutMs > 0 ? options.TimeoutMs : 45_000;
        var auth = BuildAuthParameters(options, nyxBaseUrl);
        var scopeId = options.ScopeId.Trim();
        var teamId = options.TeamId.Trim();
        var memberId = options.MemberId.Trim();
        var publishedServiceId = string.IsNullOrWhiteSpace(options.PublishedServiceId)
            ? $"member-{memberId}"
            : options.PublishedServiceId.Trim();
        var category = "feature";

        var targets = new List<StatusProbeTargetConfig>
        {
            ResponsesForwardToTeamIdentityTarget(
                nyxBaseUrl: nyxBaseUrl,
                category: category,
                intervalSeconds: intervalSeconds,
                timeoutMs: timeoutMs,
                auth: auth),
            HttpTarget(
                slug: "responses-forward-team-01-nyxid-service",
                name: "Responses -> Studio Team 01 NyxID service",
                category: category,
                url: $"{nyxBaseUrl}/api/v1/proxy/services",
                expectedStatuses: "200",
                intervalSeconds: intervalSeconds,
                timeoutMs: timeoutMs,
                parameters: AuthenticatedParameters(auth,
                    expectedBodyRegex:
                    "\"proxy_url_slug\"\\s*:\\s*\"[^\"]*/s/" +
                    Regex.Escape(serviceSlug) +
                    "/\\{path\\}\""))
        };
        targets.Add(
            HttpTarget(
                slug: "responses-forward-team-02-nyxid-proxy-models",
                name: "Responses -> Studio Team 02 NyxID proxy",
                category: category,
                url: $"{nyxBaseUrl}/api/v1/proxy/s/{Uri.EscapeDataString(serviceSlug)}/v1/models",
                expectedStatuses: "200",
                intervalSeconds: intervalSeconds,
                timeoutMs: timeoutMs,
                parameters: AuthenticatedParameters(auth,
                    expectedBodyRegex: """(?s)"data"\s*:""")));
        targets.Add(
            HttpTarget(
                slug: "responses-forward-team-03-direct-responses",
                name: "Responses -> Studio Team 03 direct /v1/responses",
                category: category,
                url: $"{directBaseUrl}/v1/responses",
                method: "POST",
                expectedStatuses: "200",
                intervalSeconds: intervalSeconds,
                timeoutMs: timeoutMs,
                parameters: AuthenticatedParameters(auth,
                    body: ResponsesBody(model, prompt),
                    expectedBodyContains: "event: response.completed",
                    forbiddenBodyContains: "event: response.failed")));
        targets.Add(
            InternalForwardTeamTarget(
                slug: "responses-forward-team-04-route-policy",
                name: "Responses -> Studio Team 04 route policy",
                category: category,
                stage: "route-policy",
                intervalSeconds: intervalSeconds,
                timeoutMs: timeoutMs,
                scopeId: scopeId,
                teamId: teamId,
                memberId: memberId,
                publishedServiceId: publishedServiceId,
                endpointId: endpointId,
                model: model,
                prompt: prompt));
        targets.Add(
            InternalForwardTeamTarget(
                slug: "responses-forward-team-05-team-entry-member",
                name: "Responses -> Studio Team 05 team entry member",
                category: category,
                stage: "team-entry-member",
                intervalSeconds: intervalSeconds,
                timeoutMs: timeoutMs,
                scopeId: scopeId,
                teamId: teamId,
                memberId: memberId,
                publishedServiceId: publishedServiceId,
                endpointId: endpointId,
                model: model,
                prompt: prompt));
        targets.Add(
            InternalForwardTeamTarget(
                slug: "responses-forward-team-06-member-binding",
                name: "Responses -> Studio Team 06 member binding",
                category: category,
                stage: "member-binding",
                intervalSeconds: intervalSeconds,
                timeoutMs: timeoutMs,
                scopeId: scopeId,
                teamId: teamId,
                memberId: memberId,
                publishedServiceId: publishedServiceId,
                endpointId: endpointId,
                model: model,
                prompt: prompt));
        targets.Add(
            InternalForwardTeamTarget(
                slug: "responses-forward-team-07-direct-team-invoke",
                name: "Responses -> Studio Team 07 direct team invoke",
                category: category,
                stage: "direct-team-invoke",
                intervalSeconds: intervalSeconds,
                timeoutMs: timeoutMs,
                scopeId: scopeId,
                teamId: teamId,
                memberId: memberId,
                publishedServiceId: publishedServiceId,
                endpointId: endpointId,
                model: model,
                prompt: prompt,
                authParameters: auth));
        targets.Add(
            HttpTarget(
                slug: "responses-forward-team-08-nyxid-proxy-e2e",
                name: "Responses -> Studio Team 08 NyxID proxy e2e",
                category: category,
                url: $"{nyxBaseUrl}/api/v1/proxy/s/{Uri.EscapeDataString(serviceSlug)}/v1/responses",
                method: "POST",
                expectedStatuses: "200",
                intervalSeconds: intervalSeconds,
                timeoutMs: timeoutMs,
                parameters: AuthenticatedParameters(auth,
                    body: ResponsesBody(model, prompt),
                    expectedBodyContains: "event: response.completed",
                    forbiddenBodyContains: "event: response.failed")));

        return targets;
    }

    private static StatusProbeTargetConfig InternalForwardTeamTarget(
        string slug,
        string name,
        string category,
        string stage,
        int intervalSeconds,
        int timeoutMs,
        string scopeId,
        string teamId,
        string memberId,
        string publishedServiceId,
        string endpointId,
        string model,
        string prompt,
        IReadOnlyDictionary<string, string>? authParameters = null)
    {
        var target = new StatusProbeTargetConfig
        {
            Slug = slug,
            Name = name,
            Category = category,
            Probe = ResponsesForwardTeamInternalProbe,
            IntervalSeconds = intervalSeconds,
            TimeoutMs = timeoutMs,
        };
        target.Parameters["Stage"] = stage;
        target.Parameters["ScopeId"] = scopeId;
        target.Parameters["TeamId"] = teamId;
        target.Parameters["MemberId"] = memberId;
        target.Parameters["PublishedServiceId"] = publishedServiceId;
        target.Parameters["EndpointId"] = endpointId;
        target.Parameters["Model"] = model;
        target.Parameters["Prompt"] = prompt;
        if (authParameters is not null)
        {
            foreach (var (key, value) in authParameters)
            {
                if (key.StartsWith("Auth.", StringComparison.OrdinalIgnoreCase))
                    target.Parameters[key] = value;
            }
        }

        return target;
    }

    private static StatusProbeTargetConfig HttpTarget(
        string slug,
        string name,
        string category,
        string url,
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

    private static Dictionary<string, string> AuthenticatedParameters(
        IReadOnlyDictionary<string, string> auth,
        string? body = null,
        string? expectedBodyContains = null,
        string? expectedBodyRegex = null,
        string? forbiddenBodyContains = null)
    {
        var parameters = new Dictionary<string, string>(auth, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(body))
            parameters["Body"] = body;
        if (!string.IsNullOrWhiteSpace(expectedBodyContains))
            parameters["ExpectedBodyContains"] = expectedBodyContains;
        if (!string.IsNullOrWhiteSpace(expectedBodyRegex))
            parameters["ExpectedBodyRegex"] = expectedBodyRegex;
        if (!string.IsNullOrWhiteSpace(forbiddenBodyContains))
            parameters["ForbiddenBodyContains"] = forbiddenBodyContains;
        return parameters;
    }

    private static Dictionary<string, string> BuildAuthParameters(
        ResponsesForwardToTeamStatusProbeOptions options,
        string nyxBaseUrl)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Auth.Mode"] = string.IsNullOrWhiteSpace(options.AuthMode)
                ? "static_bearer"
                : options.AuthMode.Trim(),
            ["Header.Authorization"] = $"Bearer {ConfigurationPlaceholder(options.AccessTokenConfigurationKey)}",
        };

        var tokenEndpoint = string.IsNullOrWhiteSpace(options.TokenEndpoint)
            ? $"{nyxBaseUrl}/oauth/token"
            : options.TokenEndpoint.Trim();
        if (!string.IsNullOrWhiteSpace(tokenEndpoint))
            parameters["Auth.TokenEndpoint"] = tokenEndpoint;
        if (!string.IsNullOrWhiteSpace(options.ClientIdConfigurationKey))
            parameters["Auth.ClientIdConfigurationKey"] = options.ClientIdConfigurationKey.Trim();
        if (!string.IsNullOrWhiteSpace(options.ClientSecretConfigurationKey))
            parameters["Auth.ClientSecretConfigurationKey"] = options.ClientSecretConfigurationKey.Trim();
        if (!string.IsNullOrWhiteSpace(options.ClientCredentialsScope))
            parameters["Auth.ClientCredentialsScope"] = options.ClientCredentialsScope.Trim();
        if (!string.IsNullOrWhiteSpace(options.AccessTokenConfigurationKey))
            parameters["Auth.StaticBearerConfigurationKey"] = options.AccessTokenConfigurationKey.Trim();

        return parameters;
    }

    private static string ConfigurationPlaceholder(string key) =>
        "${configuration:" + (string.IsNullOrWhiteSpace(key)
            ? "Aevatar:Status:ResponsesForwardToTeam:BearerToken"
            : key.Trim()) + "}";

    private static string ResponsesBody(string model, string prompt) =>
        "{\"model\":" + JsonSerializer.Serialize(model) +
        ",\"input\":" + JsonSerializer.Serialize(prompt) +
        ",\"stream\":true}";

}
