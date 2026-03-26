using Aevatar.GAgentService.Abstractions;
using Google.Protobuf.Collections;

namespace Aevatar.GAgentService.Application.Workflows;

internal static class ScopeWorkflowCapabilityConventions
{
    public static string ResolveAppId(
        ScopeWorkflowCapabilityOptions options,
        string? appId)
    {
        ArgumentNullException.ThrowIfNull(options);

        var normalized = NormalizeOptional(appId);
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = NormalizeOptional(options.AppId);

        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("appId is required.");
        if (normalized.Contains(':', StringComparison.Ordinal))
            throw new InvalidOperationException("appId must not contain ':'.");

        return normalized;
    }

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

    public static ServiceIdentity BuildIdentity(
        ScopeWorkflowCapabilityOptions options,
        string scopeId,
        string workflowId,
        string? appId = null)
    {
        var resolvedAppId = ResolveAppId(options, appId);
        return new()
        {
            TenantId = options.TenantId.Trim(),
            AppId = resolvedAppId,
            Namespace = options.BuildNamespace(scopeId),
            ServiceId = workflowId,
        };
    }

    public static string BuildDefinitionActorIdPrefix(
        ScopeWorkflowCapabilityOptions options,
        string scopeId,
        string workflowId,
        string? appId = null) =>
        options.BuildDefinitionActorIdPrefix(
            scopeId,
            workflowId,
            ResolveAppId(options, appId));
}
