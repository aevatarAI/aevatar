using System.Collections.Concurrent;
using System.Linq;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Modules;

internal static class SecureValueRuntimeStore
{
    private static readonly ConcurrentDictionary<string, string> Values = new(StringComparer.Ordinal);

    public static void Set(string? agentId, string? runId, string? variable, string value)
    {
        var key = BuildKey(agentId, runId, variable);
        if (string.IsNullOrWhiteSpace(key))
            return;

        Values[key] = value ?? string.Empty;
    }

    public static bool TryGet(string? agentId, string? runId, string? variable, out string value)
    {
        var key = BuildKey(agentId, runId, variable);
        if (string.IsNullOrWhiteSpace(key))
        {
            value = string.Empty;
            return false;
        }

        return Values.TryGetValue(key, out value!);
    }

    public static void RemoveRun(string? agentId, string? runId)
    {
        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        if (string.IsNullOrWhiteSpace(normalizedRunId))
            return;

        var normalizedAgentId = NormalizeAgentId(agentId);
        var prefix = $"{normalizedAgentId}:{normalizedRunId}:";
        foreach (var key in Values.Keys
                     .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                     .ToList())
        {
            Values.TryRemove(key, out _);
        }
    }

    private static string BuildKey(string? agentId, string? runId, string? variable)
    {
        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        var normalizedVariable = NormalizeVariable(variable);
        if (string.IsNullOrWhiteSpace(normalizedRunId) || string.IsNullOrWhiteSpace(normalizedVariable))
            return string.Empty;

        return $"{NormalizeAgentId(agentId)}:{normalizedRunId}:{normalizedVariable}";
    }

    private static string NormalizeAgentId(string? agentId) =>
        string.IsNullOrWhiteSpace(agentId) ? string.Empty : agentId.Trim();

    private static string NormalizeVariable(string? variable) =>
        string.IsNullOrWhiteSpace(variable) ? string.Empty : variable.Trim();
}
