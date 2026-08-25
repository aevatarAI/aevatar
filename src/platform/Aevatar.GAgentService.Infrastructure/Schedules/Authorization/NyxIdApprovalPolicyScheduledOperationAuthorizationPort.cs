using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;

namespace Aevatar.GAgentService.Infrastructure.Schedules.Authorization;

public sealed class NyxIdApprovalPolicyScheduledOperationAuthorizationPort
    : INyxIdScheduledOperationAuthorizationPort
{
    private const string ProxyCapabilityScope = "proxy";
    internal const long DefaultAuthorityResponseMaxBytes = 1024 * 1024;
    internal static readonly TimeSpan DefaultAuthorityReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ResourceMatchTimeout = TimeSpan.FromMilliseconds(50);
    private readonly INyxIdApiClientFactory _apiClientFactory;
    private readonly IWorkflowCallerAccessTokenProvider _accessTokenProvider;
    private readonly TimeSpan _authorityReadTimeout;
    private readonly long _authorityResponseMaxBytes;

    public NyxIdApprovalPolicyScheduledOperationAuthorizationPort(
        INyxIdApiClientFactory apiClientFactory,
        IWorkflowCallerAccessTokenProvider accessTokenProvider)
        : this(
            apiClientFactory,
            accessTokenProvider,
            DefaultAuthorityReadTimeout,
            DefaultAuthorityResponseMaxBytes)
    {
    }

    internal NyxIdApprovalPolicyScheduledOperationAuthorizationPort(
        INyxIdApiClientFactory apiClientFactory,
        IWorkflowCallerAccessTokenProvider accessTokenProvider,
        TimeSpan authorityReadTimeout,
        long authorityResponseMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(apiClientFactory);
        ArgumentNullException.ThrowIfNull(accessTokenProvider);
        if (authorityReadTimeout <= TimeSpan.Zero || authorityReadTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(authorityReadTimeout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(authorityResponseMaxBytes);

        _apiClientFactory = apiClientFactory;
        _accessTokenProvider = accessTokenProvider;
        _authorityReadTimeout = authorityReadTimeout;
        _authorityResponseMaxBytes = authorityResponseMaxBytes;
    }

    public async Task<NyxIdScheduledOperationAuthorizationResult> EvaluateAsync(
        NyxIdScheduledOperationAuthorizationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var targetUserServiceId = Require(request.Request.UserServiceId);
            var method = NyxIdRequestSelectorContract.MethodName(request.Request.Method);
            var resource = Require(request.Request.PathTemplate);
            if (method.Length == 0 || !resource.StartsWith("/", StringComparison.Ordinal))
                return Unavailable();

            using var authorityReadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            authorityReadCts.CancelAfter(_authorityReadTimeout);
            var authorityCt = authorityReadCts.Token;

            var token = await _accessTokenProvider.IssueAsync(
                new WorkflowCallerNyxIdAuthority
                {
                    Platform = Require(request.SubjectPlatform),
                    Tenant = request.SubjectTenant?.Trim() ?? string.Empty,
                    ExternalUserId = Require(request.SubjectExternalUserId),
                    BindingId = Require(request.VerifiedBindingId),
                    Scope = ProxyCapabilityScope,
                },
                authorityCt);
            if (string.IsNullOrWhiteSpace(token))
                return Unavailable();

            var normalizedToken = token.Trim();

            using var apiClient = _apiClientFactory.CreateClient();
            var configsResponse = await apiClient.ListApprovalServiceConfigsBoundedAsync(
                normalizedToken,
                _authorityResponseMaxBytes,
                authorityCt);
            if (!configsResponse.Succeeded)
                return Unavailable();

            var settingsResponse = await apiClient.GetNotificationSettingsBoundedAsync(
                normalizedToken,
                _authorityResponseMaxBytes,
                authorityCt);
            if (!settingsResponse.Succeeded)
                return Unavailable();

            var userServicesResponse = await apiClient.ListUserServicesBoundedAsync(
                normalizedToken,
                _authorityResponseMaxBytes,
                authorityCt);
            if (!userServicesResponse.Succeeded)
                return Unavailable();

            if (!TryParseServiceConfigs(
                    configsResponse.Content,
                    out var configs,
                    out var dominantOrgPolicies) ||
                !TryParseGlobalApprovalRequired(settingsResponse.Content, out var globalApprovalRequired) ||
                !TryResolveTargetUserService(
                    userServicesResponse.Content,
                    targetUserServiceId,
                    out var targetUserService))
            {
                return Unavailable();
            }

            if (IsDominatedByOrgPolicy(targetUserService!, dominantOrgPolicies))
                return Unavailable();

            if (!TryResolveConfig(configs, targetUserService!, out var config))
                return Unavailable();

            if (config is null)
            {
                return FromPolicy(
                    globalApprovalRequired ? PolicyEffect.RequireApproval : PolicyEffect.AutoAllow,
                    PolicyMode.PerRequest);
            }

            foreach (var rule in config.Rules)
            {
                if (!TryRuleMatches(rule, method, resource, out var matches))
                    return Unavailable();
                if (matches)
                    return FromPolicy(rule.Effect, rule.Mode);
            }

            if (config.DefaultEffect is { } defaultEffect)
                return FromPolicy(defaultEffect, config.Mode);

            if (config.Rules.Count == 0)
            {
                return FromPolicy(
                    config.ApprovalRequired ? PolicyEffect.RequireApproval : PolicyEffect.AutoAllow,
                    config.Mode);
            }

            return FromPolicy(PolicyEffect.AutoAllow, config.Mode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable();
        }
    }

    private static bool TryResolveConfig(
        IReadOnlyList<ServicePolicy> configs,
        TargetUserService targetUserService,
        out ServicePolicy? config)
    {
        config = null;
        var exactUserServiceMatches = configs
            .Where(candidate => string.Equals(
                candidate.UserServiceId,
                targetUserService.UserServiceId,
                StringComparison.Ordinal))
            .ToArray();
        if (exactUserServiceMatches.Length > 1)
            return false;
        if (exactUserServiceMatches.Length == 1)
        {
            config = exactUserServiceMatches[0];
            return true;
        }

        var exactStorageKeyMatches = configs
            .Where(candidate =>
                string.Equals(
                    candidate.ServiceId,
                    targetUserService.UserServiceId,
                    StringComparison.Ordinal) ||
                targetUserService.CatalogServiceId != null &&
                string.Equals(
                    candidate.ServiceId,
                    targetUserService.CatalogServiceId,
                    StringComparison.Ordinal))
            .ToArray();
        if (exactStorageKeyMatches.Length > 1)
            return false;
        config = exactStorageKeyMatches.SingleOrDefault();
        return true;
    }

    private static bool IsDominatedByOrgPolicy(
        TargetUserService targetUserService,
        IReadOnlyList<DominantOrgPolicy> dominantOrgPolicies)
    {
        if (targetUserService.OwnerOrgId is null)
            return false;

        return dominantOrgPolicies.Any(policy =>
            string.Equals(
                policy.OrgId,
                targetUserService.OwnerOrgId,
                StringComparison.Ordinal) &&
            (string.Equals(
                 policy.ServiceId,
                 targetUserService.UserServiceId,
                 StringComparison.Ordinal) ||
             targetUserService.CatalogServiceId != null &&
             string.Equals(
                 policy.ServiceId,
                 targetUserService.CatalogServiceId,
                 StringComparison.Ordinal)));
    }

    private static bool TryRuleMatches(
        ApprovalRule rule,
        string method,
        string resource,
        out bool matches)
    {
        matches = false;
        var methodMatches = rule.Methods.Count == 0 ||
                            rule.Methods.Contains("*", StringComparer.Ordinal) ||
                            rule.Methods.Contains(method, StringComparer.OrdinalIgnoreCase);
        if (!methodMatches)
            return true;

        var verb = MethodVerb(method);
        if (verb is null)
            return false;
        if (rule.Verbs.Count != 0 && !rule.Verbs.Contains(verb.Value))
            return true;

        return TryResourcePatternMatches(rule.ResourcePattern, resource, out matches);
    }

    private static bool TryResourcePatternMatches(
        string rawPattern,
        string resource,
        out bool matches)
    {
        matches = false;
        var pattern = string.IsNullOrWhiteSpace(rawPattern) ? "*" : rawPattern.Trim();
        if (pattern.Length > 256)
            return false;
        if (pattern == "*")
        {
            matches = true;
            return true;
        }

        var expression = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            switch (character)
            {
                case '\\':
                    if (++index >= pattern.Length)
                        return false;
                    expression.Append(Regex.Escape(pattern[index].ToString()));
                    break;
                case '*':
                    if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                    {
                        expression.Append(".*");
                        index++;
                    }
                    else
                    {
                        expression.Append("[^/]*");
                    }
                    break;
                case '?':
                    expression.Append("[^/]");
                    break;
                case '[':
                case ']':
                case '{':
                case '}':
                    return false;
                default:
                    expression.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }

        expression.Append('$');
        matches = Regex.IsMatch(
            resource,
            expression.ToString(),
            RegexOptions.CultureInvariant,
            ResourceMatchTimeout);
        return true;
    }

    private static PolicyVerb? MethodVerb(string method) => method.ToUpperInvariant() switch
    {
        "GET" or "HEAD" or "OPTIONS" => PolicyVerb.Read,
        "DELETE" => PolicyVerb.Destructive,
        "POST" or "PUT" or "PATCH" => PolicyVerb.Write,
        _ => null,
    };

    private static bool TryParseGlobalApprovalRequired(string json, out bool approvalRequired)
    {
        approvalRequired = false;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("approval_required", out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        approvalRequired = value.GetBoolean();
        return true;
    }

    private static bool TryParseServiceConfigs(
        string json,
        out IReadOnlyList<ServicePolicy> configs,
        out IReadOnlyList<DominantOrgPolicy> dominantOrgPolicies)
    {
        configs = [];
        dominantOrgPolicies = [];
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("configs", out var configsNode) ||
            configsNode.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<ServicePolicy>();
        foreach (var configNode in configsNode.EnumerateArray())
        {
            if (!TryParseServicePolicy(configNode, out var config))
                return false;
            parsed.Add(config!);
        }

        if (root.TryGetProperty("dominant_org_policies", out var dominantPoliciesNode))
        {
            if (dominantPoliciesNode.ValueKind != JsonValueKind.Array)
                return false;

            var parsedDominantPolicies = new List<DominantOrgPolicy>();
            foreach (var policyNode in dominantPoliciesNode.EnumerateArray())
            {
                if (policyNode.ValueKind != JsonValueKind.Object ||
                    !TryReadRequiredString(policyNode, "org_id", out var orgId) ||
                    !TryReadRequiredString(policyNode, "service_id", out var serviceId))
                {
                    return false;
                }

                parsedDominantPolicies.Add(new DominantOrgPolicy(orgId, serviceId));
            }
            dominantOrgPolicies = parsedDominantPolicies;
        }

        configs = parsed;
        return true;
    }

    private static bool TryResolveTargetUserService(
        string json,
        string targetUserServiceId,
        out TargetUserService? targetUserService)
    {
        targetUserService = null;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("services", out var servicesNode) ||
            servicesNode.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var matches = new List<TargetUserService>();
        foreach (var serviceNode in servicesNode.EnumerateArray())
        {
            if (serviceNode.ValueKind != JsonValueKind.Object ||
                !TryReadRequiredString(serviceNode, "id", out var userServiceId))
            {
                return false;
            }
            if (!string.Equals(userServiceId, targetUserServiceId, StringComparison.Ordinal))
                continue;
            if (!TryReadOptionalString(
                    serviceNode,
                    "catalog_service_id",
                    out var catalogServiceId) ||
                !TryReadBoolean(serviceNode, "is_active", out var isActive) ||
                !TryReadCredentialSource(
                    serviceNode,
                    out var ownerOrgId,
                    out var isAllowed))
            {
                return false;
            }

            matches.Add(new TargetUserService(
                userServiceId,
                catalogServiceId,
                ownerOrgId,
                isActive,
                isAllowed));
        }

        if (matches.Count != 1 || !matches[0].IsActive || !matches[0].IsAllowed)
            return false;
        targetUserService = matches[0];
        return true;
    }

    private static bool TryReadCredentialSource(
        JsonElement serviceNode,
        out string? ownerOrgId,
        out bool isAllowed)
    {
        ownerOrgId = null;
        isAllowed = true;
        if (!serviceNode.TryGetProperty("credential_source", out var sourceNode) ||
            sourceNode.ValueKind != JsonValueKind.Object ||
            !TryReadRequiredString(sourceNode, "type", out var sourceType))
        {
            return false;
        }

        if (sourceType == "personal")
            return true;
        if (sourceType != "org" ||
            !TryReadRequiredString(sourceNode, "org_id", out var orgId) ||
            !TryReadBoolean(sourceNode, "allowed", out isAllowed))
        {
            return false;
        }

        ownerOrgId = orgId;
        return true;
    }

    private static bool TryParseServicePolicy(JsonElement node, out ServicePolicy? policy)
    {
        policy = null;
        if (node.ValueKind != JsonValueKind.Object ||
            !TryReadRequiredString(node, "service_id", out var serviceId) ||
            !TryReadOptionalString(node, "user_service_id", out var userServiceId) ||
            !TryReadBoolean(node, "approval_required", out var approvalRequired) ||
            !TryReadMode(node, "approval_mode", out var mode) ||
            !TryReadEffect(node, "default_effect", optional: true, out var defaultEffect) ||
            !node.TryGetProperty("rules", out var rulesNode) ||
            rulesNode.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var rules = new List<ApprovalRule>();
        foreach (var ruleNode in rulesNode.EnumerateArray())
        {
            if (!TryParseRule(ruleNode, out var rule))
                return false;
            rules.Add(rule!);
        }

        policy = new ServicePolicy(
            serviceId,
            userServiceId,
            approvalRequired,
            mode,
            rules,
            defaultEffect);
        return true;
    }

    private static bool TryParseRule(JsonElement node, out ApprovalRule? rule)
    {
        rule = null;
        if (node.ValueKind != JsonValueKind.Object ||
            !TryReadStringArray(node, "methods", out var methods) ||
            !TryReadString(node, "resource_pattern", out var resourcePattern) ||
            !TryReadVerbArray(node, "verbs", out var verbs) ||
            !TryReadEffect(node, "effect", optional: false, out var effect) ||
            effect is null ||
            !TryReadMode(node, "mode", out var mode))
        {
            return false;
        }

        var normalizedMethods = methods
            .Select(value => value.Trim().ToUpperInvariant())
            .ToArray();
        if (normalizedMethods.Length > 16 ||
            normalizedMethods.Any(value => value is not (
                "*" or "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or
                "HEAD" or "OPTIONS" or "EXEC" or "TUNNEL")) ||
            normalizedMethods.Contains("*", StringComparer.Ordinal) && normalizedMethods.Length != 1)
        {
            return false;
        }

        rule = new ApprovalRule(
            normalizedMethods,
            resourcePattern,
            verbs,
            effect.Value,
            mode);
        return true;
    }

    private static bool TryReadRequiredString(
        JsonElement node,
        string propertyName,
        out string value)
    {
        if (!TryReadString(node, propertyName, out value))
            return false;
        return value.Length != 0;
    }

    private static bool TryReadString(
        JsonElement node,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!node.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()?.Trim() ?? string.Empty;
        return true;
    }

    private static bool TryReadOptionalString(
        JsonElement node,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!node.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString()?.Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadBoolean(
        JsonElement node,
        string propertyName,
        out bool value)
    {
        value = false;
        if (!node.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryReadStringArray(
        JsonElement node,
        string propertyName,
        out IReadOnlyList<string> values)
    {
        values = [];
        if (!node.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } value)
                return false;
            parsed.Add(value);
        }

        values = parsed;
        return true;
    }

    private static bool TryReadVerbArray(
        JsonElement node,
        string propertyName,
        out IReadOnlyList<PolicyVerb> verbs)
    {
        verbs = [];
        if (!TryReadStringArray(node, propertyName, out var values))
            return false;

        var parsed = new List<PolicyVerb>();
        foreach (var value in values)
        {
            if (!TryParseVerb(value, out var verb))
                return false;
            parsed.Add(verb);
        }

        verbs = parsed;
        return true;
    }

    private static bool TryReadMode(
        JsonElement node,
        string propertyName,
        out PolicyMode mode)
    {
        mode = PolicyMode.PerRequest;
        return node.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               TryParseMode(property.GetString(), out mode);
    }

    private static bool TryReadEffect(
        JsonElement node,
        string propertyName,
        bool optional,
        out PolicyEffect? effect)
    {
        effect = null;
        if (!node.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return optional;
        }

        return property.ValueKind == JsonValueKind.String &&
               TryParseEffect(property.GetString(), out effect);
    }

    private static bool TryParseMode(string? value, out PolicyMode mode)
    {
        mode = value switch
        {
            "per_request" => PolicyMode.PerRequest,
            "grant" => PolicyMode.Grant,
            _ => default,
        };
        return value is "per_request" or "grant";
    }

    private static bool TryParseEffect(string? value, out PolicyEffect? effect)
    {
        effect = value switch
        {
            "require_approval" => PolicyEffect.RequireApproval,
            "auto_allow" => PolicyEffect.AutoAllow,
            "deny" => PolicyEffect.Deny,
            _ => null,
        };
        return effect is not null;
    }

    private static bool TryParseVerb(string value, out PolicyVerb verb)
    {
        verb = value switch
        {
            "read" => PolicyVerb.Read,
            "write" => PolicyVerb.Write,
            "destructive" => PolicyVerb.Destructive,
            _ => default,
        };
        return value is "read" or "write" or "destructive";
    }

    private static NyxIdScheduledOperationAuthorizationResult FromPolicy(
        PolicyEffect effect,
        PolicyMode mode) => new(effect switch
    {
        PolicyEffect.AutoAllow => NyxIdScheduledOperationAuthorizationDecision.AutoAllow,
        PolicyEffect.Deny => NyxIdScheduledOperationAuthorizationDecision.Denied,
        PolicyEffect.RequireApproval when mode == PolicyMode.Grant =>
            NyxIdScheduledOperationAuthorizationDecision.ReusableGrantRequired,
        PolicyEffect.RequireApproval =>
            NyxIdScheduledOperationAuthorizationDecision.PerRequestApprovalRequired,
        _ => NyxIdScheduledOperationAuthorizationDecision.AuthorityContractUnavailable,
    });

    private static NyxIdScheduledOperationAuthorizationResult Unavailable() =>
        new(NyxIdScheduledOperationAuthorizationDecision.AuthorityContractUnavailable);

    private static string Require(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("nyxid_operation_authority_context_incomplete")
            : value.Trim();

    private sealed record ServicePolicy(
        string ServiceId,
        string? UserServiceId,
        bool ApprovalRequired,
        PolicyMode Mode,
        IReadOnlyList<ApprovalRule> Rules,
        PolicyEffect? DefaultEffect);

    private sealed record DominantOrgPolicy(string OrgId, string ServiceId);

    private sealed record TargetUserService(
        string UserServiceId,
        string? CatalogServiceId,
        string? OwnerOrgId,
        bool IsActive,
        bool IsAllowed);

    private sealed record ApprovalRule(
        IReadOnlyList<string> Methods,
        string ResourcePattern,
        IReadOnlyList<PolicyVerb> Verbs,
        PolicyEffect Effect,
        PolicyMode Mode);

    private enum PolicyMode
    {
        PerRequest,
        Grant,
    }

    private enum PolicyEffect
    {
        RequireApproval,
        AutoAllow,
        Deny,
    }

    private enum PolicyVerb
    {
        Read,
        Write,
        Destructive,
    }
}
