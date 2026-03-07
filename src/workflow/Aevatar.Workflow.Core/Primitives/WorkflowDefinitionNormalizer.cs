using Aevatar.AI.Abstractions.Agents;
using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace Aevatar.Workflow.Core.Primitives;

internal sealed class WorkflowDefinitionNormalizer
{
    private static readonly (string Key, Func<WorkflowRawStep, object?> Getter)[] RootParameterMappings =
    [
        ("workers", static step => step.Workers),
        ("parallel_count", static step => step.ParallelCount),
        ("count", static step => step.Count),
        ("vote_step_type", static step => step.VoteStepType),
        ("delimiter", static step => step.Delimiter),
        ("sub_step_type", static step => step.SubStepType),
        ("sub_target_role", static step => step.SubTargetRole),
        ("map_step_type", static step => step.MapStepType),
        ("map_target_role", static step => step.MapTargetRole),
        ("reduce_step_type", static step => step.ReduceStepType),
        ("reduce_target_role", static step => step.ReduceTargetRole),
        ("reduce_prompt_prefix", static step => step.ReducePromptPrefix),
        ("signal_name", static step => step.SignalName),
        ("prompt", static step => step.Prompt),
        ("timeout", static step => step.Timeout),
        ("timeout_seconds", static step => step.TimeoutSeconds),
        ("duration_ms", static step => step.DurationMs),
        ("variable", static step => step.Variable),
        ("on_timeout", static step => step.OnTimeout),
        ("on_reject", static step => step.OnReject),
        ("workflow", static step => step.Workflow),
        ("lifecycle", static step => step.Lifecycle),
        ("query", static step => step.Query),
        ("top_k", static step => step.TopK),
        ("facts", static step => step.Facts),
    ];

    public WorkflowDefinition Normalize(WorkflowRawDefinition raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        return new WorkflowDefinition
        {
            Name = raw.Name ?? throw new InvalidOperationException("缺少 name"),
            Description = raw.Description ?? string.Empty,
            Roles = (raw.Roles ?? []).Select(MapRole).ToList(),
            Steps = (raw.Steps ?? []).Select(MapStep).ToList(),
            Configuration = new WorkflowRuntimeConfiguration
            {
                ClosedWorldMode = raw.Configuration?.ClosedWorldMode ?? false,
            },
        };
    }

    private static RoleDefinition MapRole(WorkflowRawRole role)
    {
        var eventModules = PreferTopLevelText(role.EventModules, role.Extensions?.EventModules);
        var eventRoutes = PreferTopLevelText(role.EventRoutes, role.Extensions?.EventRoutes);

        var normalized = RoleConfigurationNormalizer.Normalize(new RoleConfigurationInput
        {
            Id = role.Id,
            Name = role.Name,
            SystemPrompt = role.SystemPrompt,
            Provider = role.Provider,
            Model = role.Model,
            Temperature = role.Temperature,
            MaxTokens = role.MaxTokens,
            MaxToolRounds = role.MaxToolRounds,
            MaxHistoryMessages = role.MaxHistoryMessages,
            StreamBufferCapacity = role.StreamBufferCapacity,
            EventModules = eventModules,
            EventRoutes = eventRoutes,
            Connectors = role.Connectors,
        });

        return new RoleDefinition
        {
            Id = normalized.Id,
            Name = normalized.Name,
            SystemPrompt = normalized.SystemPrompt,
            Provider = normalized.Provider,
            Model = normalized.Model,
            Temperature = normalized.Temperature,
            MaxTokens = normalized.MaxTokens,
            MaxToolRounds = normalized.MaxToolRounds,
            MaxHistoryMessages = normalized.MaxHistoryMessages,
            StreamBufferCapacity = normalized.StreamBufferCapacity,
            EventModules = normalized.EventModules,
            EventRoutes = normalized.EventRoutes,
            Connectors = normalized.Connectors.ToList(),
        };
    }

    private static string? PreferTopLevelText(string? topLevel, string? fallback)
    {
        var primary = NormalizeText(topLevel);
        return primary ?? NormalizeText(fallback);
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    private static StepDefinition MapStep(WorkflowRawStep step)
    {
        var rawType = step.Type ?? "llm_call";
        var normalizedRawType = rawType.Trim().ToLowerInvariant();
        var canonicalType = WorkflowPrimitiveCatalog.ToCanonicalType(rawType);
        var parameters = NormalizeParameters(step.Parameters);

        ApplyErgonomicDefaults(normalizedRawType, parameters);
        LiftRootPrimitiveParameters(canonicalType, step, parameters);

        return new StepDefinition
        {
            Id = step.Id ?? throw new InvalidOperationException("step 缺 id"),
            Type = canonicalType,
            TargetRole = step.TargetRole ?? step.Role,
            Parameters = WorkflowPrimitiveCatalog.CanonicalizeStepTypeParameters(parameters),
            Next = step.Next,
            Children = step.Children?.Select(MapStep).ToList(),
            Branches = NormalizeBranches(step.Branches),
            Retry = MapRetry(step.Retry),
            OnError = MapOnError(step.OnError),
            TimeoutMs = step.TimeoutMs,
        };
    }

    private static void ApplyErgonomicDefaults(string normalizedRawType, IDictionary<string, string> parameters)
    {
        if (string.IsNullOrWhiteSpace(normalizedRawType))
            return;

        switch (normalizedRawType)
        {
            case "http_get":
                AddIfMissing(parameters, "method", "GET");
                break;
            case "http_post":
                AddIfMissing(parameters, "method", "POST");
                break;
            case "http_put":
                AddIfMissing(parameters, "method", "PUT");
                break;
            case "http_delete":
                AddIfMissing(parameters, "method", "DELETE");
                break;
            case "mcp_call":
                if (!parameters.ContainsKey("operation") &&
                    !parameters.ContainsKey("action") &&
                    parameters.TryGetValue("tool", out var toolName) &&
                    !string.IsNullOrWhiteSpace(toolName))
                {
                    parameters["operation"] = toolName;
                }
                break;
            case "foreach_llm":
                AddIfMissing(parameters, "sub_step_type", "llm_call");
                break;
            case "map_reduce_llm":
                AddIfMissing(parameters, "map_step_type", "llm_call");
                AddIfMissing(parameters, "reduce_step_type", "llm_call");
                break;
        }
    }

    private static Dictionary<string, string> NormalizeParameters(
        IReadOnlyDictionary<string, object?>? rawParameters)
    {
        if (rawParameters == null || rawParameters.Count == 0)
            return [];

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in rawParameters)
            normalized[key] = ConvertValueToString(value);
        return normalized;
    }

    private static void LiftRootPrimitiveParameters(
        string canonicalType,
        WorkflowRawStep step,
        IDictionary<string, string> parameters)
    {
        foreach (var (key, getter) in RootParameterMappings)
            AddIfMissing(parameters, key, getter(step));

        if (!parameters.ContainsKey("timeout_ms") &&
            step.TimeoutMs is not null &&
            ShouldLiftTimeoutMsToParameter(canonicalType))
        {
            parameters["timeout_ms"] = ConvertValueToString(step.TimeoutMs.Value);
        }
    }

    private static bool ShouldLiftTimeoutMsToParameter(string canonicalType) =>
        canonicalType is "wait_signal" or "connector_call" or "llm_call" or "human_input" or "human_approval";

    private static void AddIfMissing(
        IDictionary<string, string> parameters,
        string key,
        object? value)
    {
        if (parameters.ContainsKey(key) || value == null)
            return;

        var serialized = ConvertValueToString(value);
        if (string.IsNullOrWhiteSpace(serialized))
            return;

        parameters[key] = serialized;
    }

    private static Dictionary<string, string>? NormalizeBranches(object? rawBranches)
    {
        if (rawBranches == null)
            return null;

        if (rawBranches is IDictionary mapping)
            return NormalizeDictBranches(mapping);

        if (rawBranches is IEnumerable sequence && rawBranches is not string)
            return NormalizeListBranches(sequence);

        return null;
    }

    private static Dictionary<string, string>? NormalizeDictBranches(IDictionary mapping)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in mapping)
        {
            var branchKey = ConvertValueToString(entry.Key);
            var target = ResolveBranchTarget(entry.Value);
            if (!string.IsNullOrWhiteSpace(branchKey) && !string.IsNullOrWhiteSpace(target))
                normalized[branchKey] = target;
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static Dictionary<string, string>? NormalizeListBranches(IEnumerable sequence)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in sequence)
        {
            if (item is not IDictionary branchItem)
                continue;

            var branchKey = TryReadMappingValue(branchItem, "condition", "when", "case", "label", "if");
            var target = TryReadMappingValue(branchItem, "next", "to", "target", "step");
            if (!string.IsNullOrWhiteSpace(branchKey) && !string.IsNullOrWhiteSpace(target))
                normalized[branchKey] = target;
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static string ResolveBranchTarget(object? rawValue)
    {
        if (rawValue is IDictionary mapping)
        {
            var target = TryReadMappingValue(mapping, "next", "to", "target", "step");
            if (!string.IsNullOrWhiteSpace(target))
                return target;
        }

        return ConvertValueToString(rawValue);
    }

    private static string? TryReadMappingValue(IDictionary mapping, params string[] candidates)
    {
        foreach (DictionaryEntry entry in mapping)
        {
            var key = ConvertValueToString(entry.Key);
            if (!candidates.Any(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)))
                continue;

            var value = ResolveBranchTarget(entry.Value);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string ConvertValueToString(object? value)
    {
        if (value == null)
            return string.Empty;

        if (value is string text)
            return text;

        if (value is bool flag)
            return flag ? "true" : "false";

        if (value is IDictionary || value is IEnumerable and not string)
            return JsonSerializer.Serialize(NormalizeYamlValue(value));

        if (value is IFormattable formattable)
            return formattable.ToString(null, CultureInfo.InvariantCulture);

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static object? NormalizeYamlValue(object? value)
    {
        if (value == null)
            return null;

        if (value is IDictionary mapping)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in mapping)
                dict[ConvertValueToString(entry.Key)] = NormalizeYamlValue(entry.Value);
            return dict;
        }

        if (value is IEnumerable sequence && value is not string)
        {
            var list = new List<object?>();
            foreach (var item in sequence)
                list.Add(NormalizeYamlValue(item));
            return list;
        }

        return value;
    }

    private static StepRetryPolicy? MapRetry(WorkflowRawRetry? retry) =>
        retry == null ? null : new StepRetryPolicy
        {
            MaxAttempts = retry.MaxAttempts ?? 3,
            Backoff = retry.Backoff ?? "fixed",
            DelayMs = retry.DelayMs ?? 1000,
        };

    private static StepErrorPolicy? MapOnError(WorkflowRawOnError? onError) =>
        onError == null ? null : new StepErrorPolicy
        {
            Strategy = onError.Strategy ?? "fail",
            FallbackStep = onError.FallbackStep,
            DefaultOutput = onError.DefaultOutput,
        };
}
