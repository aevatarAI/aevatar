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

    public static ServiceIdentity BuildIdentity(
        ScopeWorkflowCapabilityOptions options,
        string scopeId,
        string workflowId) =>
        new()
        {
            TenantId = options.TenantId.Trim(),
            AppId = options.AppId.Trim(),
            Namespace = options.BuildNamespace(scopeId),
            ServiceId = workflowId,
        };
}
