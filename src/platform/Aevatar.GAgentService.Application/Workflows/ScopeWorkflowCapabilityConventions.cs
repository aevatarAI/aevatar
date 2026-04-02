using Aevatar.GAgentService.Abstractions;
using Google.Protobuf.Collections;

namespace Aevatar.GAgentService.Application.Workflows;

internal static class ScopeWorkflowCapabilityConventions
{
    public static string NormalizeWorkflowId(string workflowId)
    {
        var normalized = ScopeWorkflowCapabilityOptions.NormalizeRequired(workflowId, nameof(workflowId));
        if (normalized.Contains(':', StringComparison.Ordinal))
            throw new InvalidOperationException("workflowId must not contain ':'.");

        return normalized;
    }

    public static string ResolveDisplayName(string? displayName, string workflowId)
    {
        var normalized = NormalizeOptional(displayName);
        return string.IsNullOrWhiteSpace(normalized) ? workflowId : normalized;
    }

    public static string ResolveRevisionId(string? revisionId)
    {
        var normalized = NormalizeOptional(revisionId);
        return !string.IsNullOrWhiteSpace(normalized)
            ? normalized
            : $"rev-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
    }

    public static void AddInlineWorkflowYamls(
        MapField<string, string> target,
        IReadOnlyDictionary<string, string>? source)
    {
        if (source == null)
            return;

        foreach (var (key, value) in source)
        {
            var normalizedKey = NormalizeOptional(key);
            var normalizedValue = NormalizeOptional(value);
            if (string.IsNullOrWhiteSpace(normalizedKey) || string.IsNullOrWhiteSpace(normalizedValue))
                continue;

            target[normalizedKey] = normalizedValue;
        }
    }

    public static string NormalizeOptional(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>
    /// Resolves the effective app_id: uses the provided value when non-empty,
    /// otherwise falls back to <see cref="ScopeWorkflowCapabilityOptions.ServiceAppId"/> ("default").
    /// </summary>
    public static string ResolveAppId(ScopeWorkflowCapabilityOptions options, string? appId)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalized = NormalizeOptional(appId);
        return string.IsNullOrWhiteSpace(normalized)
            ? ScopeWorkflowCapabilityOptions.NormalizeRequired(options.ServiceAppId, nameof(options.ServiceAppId))
            : normalized;
    }

    public static ServiceIdentity BuildIdentity(
        ScopeWorkflowCapabilityOptions options,
        string scopeId,
        string workflowId,
        string? appId = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedWorkflowId = NormalizeWorkflowId(workflowId);
        return new()
        {
            TenantId = normalizedScopeId,
            AppId = ResolveAppId(options, appId),
            Namespace = ScopeWorkflowCapabilityOptions.NormalizeRequired(options.ServiceNamespace, nameof(options.ServiceNamespace)),
            ServiceId = normalizedWorkflowId,
        };
    }

    public static ServiceIdentity BuildServiceIdentity(
        ScopeWorkflowCapabilityOptions options,
        string scopeId,
        string serviceId,
        string? appId = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedServiceId = ScopeWorkflowCapabilityOptions.NormalizeRequired(serviceId, nameof(serviceId));
        return new()
        {
            TenantId = normalizedScopeId,
            AppId = ResolveAppId(options, appId),
            Namespace = ScopeWorkflowCapabilityOptions.NormalizeRequired(options.ServiceNamespace, nameof(options.ServiceNamespace)),
            ServiceId = normalizedServiceId,
        };
    }

    public static ServiceIdentity BuildDefaultServiceIdentity(
        ScopeWorkflowCapabilityOptions options,
        string scopeId,
        string? appId = null) =>
        BuildServiceIdentity(
            options,
            scopeId,
            ScopeWorkflowCapabilityOptions.NormalizeRequired(options.DefaultServiceId, nameof(options.DefaultServiceId)),
            appId);

    public static string BuildDefaultDefinitionActorIdPrefix(
        ScopeWorkflowCapabilityOptions options,
        string scopeId) =>
        BuildDefinitionActorIdPrefix(
            options,
            scopeId,
            ScopeWorkflowCapabilityOptions.NormalizeRequired(options.DefaultServiceId, nameof(options.DefaultServiceId)));

    public static string BuildDefinitionActorIdPrefix(
        ScopeWorkflowCapabilityOptions options,
        string scopeId,
        string workflowId) =>
        options.BuildDefinitionActorIdPrefix(scopeId, workflowId);
}
