namespace Aevatar.Workflow.Core.Primitives;

/// <summary>
/// Central primitive policy for workflow step types.
/// Keeps alias canonicalization and closed-world restrictions in one place.
/// </summary>
public static class WorkflowPrimitiveCatalog
{
    private static readonly IReadOnlyDictionary<string, string> CanonicalTypeMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["loop"] = "while",
            ["sub_workflow"] = "workflow_call",
            ["for_each"] = "foreach",
            ["foreach_llm"] = "foreach",
            ["parallel_fanout"] = "parallel",
            ["fan_out"] = "parallel",
            ["mapreduce"] = "map_reduce",
            ["map_reduce_llm"] = "map_reduce",
            ["judge"] = "evaluate",
            ["select"] = "race",
            ["assert"] = "guard",
            ["sleep"] = "delay",
            ["publish"] = "emit",
            ["wait"] = "wait_signal",
            ["bridge_call"] = "connector_call",
            ["cli_call"] = "connector_call",
            ["mcp_call"] = "connector_call",
            ["http_get"] = "connector_call",
            ["http_post"] = "connector_call",
            ["http_put"] = "connector_call",
            ["http_delete"] = "connector_call",
            // Keep runtime module matching stable: VoteConsensusModule currently handles "vote".
            ["vote_consensus"] = "vote",
        };

    private static readonly HashSet<string> ClosedWorldBlockedCanonicalTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "llm_call",
        "tool_call",
        "connector_call",
        "evaluate",
        "reflect",
        "human_input",
        "human_approval",
        "wait_signal",
        "emit",
        "parallel",
        "race",
        "map_reduce",
        "vote",
        "foreach",
        "dynamic_workflow",
    };

    private static readonly string[] IdentityPrimitives =
    [
        "transform", "assign", "retrieve_facts", "cache",
        "conditional", "switch", "checkpoint",
        "workflow_yaml_validate",
    ];

    public static IReadOnlySet<string> BuiltInCanonicalTypes { get; } = DeriveBuiltInCanonicalTypes();

    private static HashSet<string> DeriveBuiltInCanonicalTypes()
    {
        var set = new HashSet<string>(ClosedWorldBlockedCanonicalTypes, StringComparer.OrdinalIgnoreCase);
        foreach (var canonical in CanonicalTypeMap.Values)
            set.Add(canonical);
        foreach (var identity in IdentityPrimitives)
            set.Add(identity);
        return set;
    }

    public static string ToCanonicalType(string? stepType)
    {
        var normalized = (stepType ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
            return normalized;

        return CanonicalTypeMap.TryGetValue(normalized, out var canonical)
            ? canonical
            : normalized;
    }

    public static bool IsClosedWorldBlocked(string? stepType) =>
        ClosedWorldBlockedCanonicalTypes.Contains(ToCanonicalType(stepType));

    public static bool IsStepTypeParameterKey(string key) =>
        key.EndsWith("_step_type", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("step", StringComparison.OrdinalIgnoreCase);

    public static ISet<string> BuildCanonicalStepTypeSet(IEnumerable<string>? stepTypes)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (stepTypes == null)
            return set;

        foreach (var stepType in stepTypes)
        {
            var canonical = ToCanonicalType(stepType);
            if (!string.IsNullOrWhiteSpace(canonical))
                set.Add(canonical);
        }

        return set;
    }

    public static bool IsKnownStepType(string? stepType, ISet<string>? knownCanonicalStepTypes)
    {
        if (knownCanonicalStepTypes == null || knownCanonicalStepTypes.Count == 0)
            return true;

        var canonical = ToCanonicalType(stepType);
        return !string.IsNullOrWhiteSpace(canonical) &&
               knownCanonicalStepTypes.Contains(canonical);
    }

    public static Dictionary<string, string> CanonicalizeStepTypeParameters(
        IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters == null || parameters.Count == 0)
            return [];

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            normalized[key] = IsStepTypeParameterKey(key)
                ? ToCanonicalType(value)
                : value;
        }

        return normalized;
    }
}
