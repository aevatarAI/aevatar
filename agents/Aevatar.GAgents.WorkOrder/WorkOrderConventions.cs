using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgents.WorkOrder;

public static class WorkOrderConventions
{
    public const string ActorIdPrefix = "work-order";
    public const string ExecutionWorkerPublisherActorId = "studio.work-order-execution-worker";

    public static string BuildWorkOrderId(string scopeId, string dedupKey)
    {
        var normalizedScopeId = NormalizeScopeId(scopeId);
        var normalizedDedupKey = NormalizeRequired(dedupKey, nameof(dedupKey));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{normalizedScopeId}\n{normalizedDedupKey}"));
        return $"wo-{Convert.ToHexStringLower(digest)}";
    }

    public static string BuildActorId(string scopeId, string workOrderId) =>
        $"{ActorIdPrefix}:{NormalizeScopeId(scopeId)}:{NormalizeWorkOrderId(workOrderId)}";

    public static string BuildDispatchCommandId(string workOrderId) =>
        $"work-order-dispatch-{NormalizeWorkOrderId(workOrderId)}";

    public static string BuildRequestedRunId(string workOrderId) =>
        $"work-order-run-{NormalizeWorkOrderId(workOrderId)}";

    public static string BuildTerminalDeliveryId(string workOrderId) =>
        $"work-order-terminal-{NormalizeWorkOrderId(workOrderId)}";

    public static string NormalizeScopeId(string? scopeId)
    {
        var normalized = NormalizeRequired(scopeId, nameof(scopeId));
        if (normalized.Contains(':'))
            throw new ArgumentException("scopeId must not contain ':' (it is the actor-id separator).", nameof(scopeId));
        return normalized;
    }

    public static string NormalizeWorkOrderId(string? workOrderId)
    {
        var normalized = NormalizeRequired(workOrderId, nameof(workOrderId));
        if (normalized.Contains(':'))
            throw new ArgumentException("workOrderId must not contain ':' (it is the actor-id separator).", nameof(workOrderId));
        return normalized;
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        return normalized;
    }
}
