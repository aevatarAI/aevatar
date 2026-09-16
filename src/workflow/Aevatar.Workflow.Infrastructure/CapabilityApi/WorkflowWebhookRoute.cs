using System.Text;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

/// <summary>
/// Canonical identity for the global workflow-webhook route namespace.
/// Route parameters, dynamic-store keys, and static configuration must all
/// use the same representation or the same URI can resolve to two owners.
/// </summary>
internal static class WorkflowWebhookRoute
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().ToLowerInvariant();
        return Encoding.UTF8.GetByteCount(normalized) <= WorkflowWebhookIngressLimits.MaxRouteKeyBytes
            ? normalized
            : null;
    }
}
