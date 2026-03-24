using System.Collections.Immutable;

namespace Aevatar.Studio.Domain.Studio.Compatibility;

public sealed class WorkflowCompatibilityProfile
{
    public static WorkflowCompatibilityProfile AevatarV1 { get; } = CreateAevatarV1();

    public required string Version { get; init; }

    public required ImmutableHashSet<string> AllowedRootFields { get; init; }

    public required ImmutableHashSet<string> AllowedConfigurationFields { get; init; }

    public required ImmutableHashSet<string> AllowedRoleFields { get; init; }

    public required ImmutableHashSet<string> AllowedRoleExtensionFields { get; init; }

    public required ImmutableHashSet<string> AllowedStepFields { get; init; }

    public required ImmutableHashSet<string> AllowedRetryFields { get; init; }

    public required ImmutableHashSet<string> AllowedOnErrorFields { get; init; }

    public required ImmutableHashSet<string> AllowedBranchListFields { get; init; }

    public required ImmutableDictionary<string, string> AliasToCanonicalType { get; init; }

    public required ImmutableHashSet<string> CanonicalStepTypes { get; init; }

    public required ImmutableHashSet<string> AdvancedImportOnlyStepTypes { get; init; }

    public required ImmutableHashSet<string> ForbiddenAuthoringStepTypes { get; init; }

    public required ImmutableHashSet<string> ClosedWorldBlockedStepTypes { get; init; }

    public required ImmutableHashSet<string> RootParameterFields { get; init; }

    public required ImmutableHashSet<string> SupportedWorkflowCallLifecycles { get; init; }

    public required ImmutableHashSet<string> ExpressionFunctions { get; init; }

    public string ToCanonicalType(string? value)
    {
        var normalized = NormalizeToken(value);
        if (string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }

        return AliasToCanonicalType.TryGetValue(normalized, out var canonical)
            ? canonical
            : normalized;
    }

    public bool IsKnownStepType(string? value)
    {
        var canonical = ToCanonicalType(value);
        return !string.IsNullOrWhiteSpace(canonical) &&
               (CanonicalStepTypes.Contains(canonical) ||
                AdvancedImportOnlyStepTypes.Contains(canonical) ||
                ForbiddenAuthoringStepTypes.Contains(canonical));
    }

    public bool IsCanonicalStepType(string? value)
    {
        var canonical = ToCanonicalType(value);
        return !string.IsNullOrWhiteSpace(canonical) && CanonicalStepTypes.Contains(canonical);
    }

    public bool IsAdvancedImportOnly(string? value) =>
        AdvancedImportOnlyStepTypes.Contains(ToCanonicalType(value));

    public bool IsForbiddenAuthoringType(string? value) =>
        ForbiddenAuthoringStepTypes.Contains(ToCanonicalType(value));

    public bool IsClosedWorldBlocked(string? value) =>
        ClosedWorldBlockedStepTypes.Contains(ToCanonicalType(value));

    public bool IsStepTypeParameterKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return key.EndsWith("_step_type", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "step", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsSupportedWorkflowCallLifecycle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return SupportedWorkflowCallLifecycles.Contains(NormalizeToken(value));
    }

    public bool ShouldMirrorTimeoutMsToParameters(string? canonicalType) =>
        ToCanonicalType(canonicalType) is "wait_signal" or "connector_call" or "llm_call" or "human_input" or "human_approval";

    private static WorkflowCompatibilityProfile CreateAevatarV1()
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var rootParameterFields = ImmutableHashSet.Create(
            comparer,
            "workers",
            "parallel_count",
            "count",
            "vote_step_type",
            "delimiter",
            "sub_step_type",
            "sub_target_role",
            "map_step_type",
            "map_target_role",
            "reduce_step_type",
            "reduce_target_role",
            "reduce_prompt_prefix",
            "signal_name",
            "prompt",
            "timeout",
            "timeout_seconds",
            "duration_ms",
            "variable",
            "on_timeout",
            "on_reject",
            "workflow",
            "lifecycle",
            "query",
            "top_k",
            "facts");

        var canonicalTypes = ImmutableHashSet.Create(
            comparer,
            "transform",
            "assign",
            "retrieve_facts",
            "cache",
            "guard",
            "conditional",
            "switch",
            "while",
            "delay",
            "wait_signal",
            "checkpoint",
            "llm_call",
            "tool_call",
            "evaluate",
            "reflect",
            "foreach",
            "parallel",
            "race",
            "map_reduce",
            "workflow_call",
            "dynamic_workflow",
            "vote",
            "connector_call",
            "emit",
            "human_input",
            "human_approval",
            "workflow_yaml_validate");

        return new WorkflowCompatibilityProfile
        {
            Version = "aevatar.workflow.v1",
            AllowedRootFields = ImmutableHashSet.Create(comparer, "name", "description", "configuration", "roles", "steps"),
            AllowedConfigurationFields = ImmutableHashSet.Create(comparer, "closed_world_mode"),
            AllowedRoleFields = ImmutableHashSet.Create(
                comparer,
                "id",
                "name",
                "system_prompt",
                "provider",
                "model",
                "temperature",
                "max_tokens",
                "max_tool_rounds",
                "max_history_messages",
                "stream_buffer_capacity",
                "event_modules",
                "event_routes",
                "connectors",
                "extensions"),
            AllowedRoleExtensionFields = ImmutableHashSet.Create(comparer, "event_modules", "event_routes"),
            AllowedStepFields = ImmutableHashSet.Create(
                comparer,
                ["id", "type", "target_role", "role", "parameters", "next", "branches", "children", "retry", "on_error", "timeout_ms", .. rootParameterFields]),
            AllowedRetryFields = ImmutableHashSet.Create(comparer, "max_attempts", "backoff", "delay_ms"),
            AllowedOnErrorFields = ImmutableHashSet.Create(comparer, "strategy", "fallback_step", "default_output"),
            AllowedBranchListFields = ImmutableHashSet.Create(comparer, "condition", "when", "case", "label", "if", "next", "to", "target", "step"),
            AliasToCanonicalType = ImmutableDictionary.CreateRange(
                comparer,
                new Dictionary<string, string>(comparer)
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
                    ["vote_consensus"] = "vote",
                }),
            CanonicalStepTypes = canonicalTypes,
            AdvancedImportOnlyStepTypes = ImmutableHashSet.Create(comparer, "actor_send"),
            ForbiddenAuthoringStepTypes = ImmutableHashSet.Create(comparer, "workflow_loop"),
            ClosedWorldBlockedStepTypes = ImmutableHashSet<string>.Empty.WithComparer(comparer),
            RootParameterFields = rootParameterFields,
            SupportedWorkflowCallLifecycles = ImmutableHashSet.Create(comparer, "singleton", "transient", "scope"),
            ExpressionFunctions = ImmutableHashSet.Create(
                comparer,
                "if",
                "concat",
                "isBlank",
                "length",
                "not",
                "and",
                "or",
                "upper",
                "lower",
                "trim",
                "add",
                "sub",
                "mul",
                "div",
                "eq",
                "lt",
                "lte",
                "gt",
                "gte"),
        };
    }

    private static string NormalizeToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}
