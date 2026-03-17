// ─────────────────────────────────────────────────────────────
// WorkflowParser — YAML 工作流解析器
// 将 YAML 文本反序列化为 WorkflowDefinition 及嵌套步骤结构
// ─────────────────────────────────────────────────────────────

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Aevatar.AI.Abstractions.Agents;
using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace Aevatar.Workflow.Core.Primitives;

/// <summary>
/// YAML 工作流解析器。使用 snake_case 命名约定将 YAML 解析为强类型工作流定义。
/// </summary>
public sealed class WorkflowParser
{
    private static readonly IDeserializer D = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance).Build();
    private static readonly (string Key, Func<RawStep, object?> Getter)[] RootParameterMappings =
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

    /// <summary>
    /// 解析 YAML 字符串为工作流定义。
    /// </summary>
    /// <param name="yaml">YAML 格式的工作流配置文本。</param>
    /// <returns>解析后的工作流定义。</returns>
    /// <exception cref="InvalidOperationException">YAML 为空或缺少必填字段时抛出。</exception>
    public WorkflowDefinition Parse(string yaml)
    {
        var raw = D.Deserialize<Raw>(yaml) ?? throw new InvalidOperationException("YAML 为空");
        return new WorkflowDefinition
        {
            Name = raw.Name ?? throw new InvalidOperationException("缺少 name"),
            Description = raw.Description ?? "",
            Roles = (raw.Roles ?? []).Select(MapRole).ToList(),
            Steps = (raw.Steps ?? []).Select(MapStep).ToList(),
            Configuration = new WorkflowRuntimeConfiguration
            {
                ClosedWorldMode = raw.Configuration?.ClosedWorldMode ?? false,
            },
        };
    }

    private static RoleDefinition MapRole(RawRole role)
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

    private static StepDefinition MapStep(RawStep s)
    {
        var rawType = s.Type ?? "llm_call";
        var normalizedRawType = rawType.Trim().ToLowerInvariant();
        var canonicalType = WorkflowPrimitiveCatalog.ToCanonicalType(rawType);
        var parameters = NormalizeParameters(s.Parameters);

        ApplyErgonomicDefaults(normalizedRawType, parameters);
        LiftRootPrimitiveParameters(canonicalType, s, parameters);

        return new StepDefinition
        {
            Id = s.Id ?? throw new InvalidOperationException("step 缺 id"),
            Type = canonicalType,
            TargetRole = s.TargetRole ?? s.Role,
            Parameters = WorkflowPrimitiveCatalog.CanonicalizeStepTypeParameters(parameters),
            Next = s.Next,
            Children = s.Children?.Select(MapStep).ToList(),
            Branches = NormalizeBranches(s.Branches),
            Retry = MapRetry(s.Retry),
            OnError = MapOnError(s.OnError),
            TimeoutMs = s.TimeoutMs,
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
                // Accept mcp_call + tool, then forward as connector_call.operation for consistent metadata.
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
        RawStep s,
        IDictionary<string, string> parameters)
    {
        // Common LLM output pattern: puts primitive params at step root instead of parameters.
        foreach (var (key, getter) in RootParameterMappings)
            AddIfMissing(parameters, key, getter(s));

        // Root timeout_ms may be either primitive parameter or step timeout.
        // Keep step timeout via StepDefinition.TimeoutMs, and also mirror to parameters
        // for primitives that naturally consume timeout_ms.
        if (!parameters.ContainsKey("timeout_ms") &&
            s.TimeoutMs is not null &&
            ShouldLiftTimeoutMsToParameter(canonicalType))
        {
            parameters["timeout_ms"] = ConvertValueToString(s.TimeoutMs.Value);
        }
    }

    private static bool ShouldLiftTimeoutMsToParameter(string canonicalType) =>
        canonicalType is "wait_signal" or "connector_call" or "secure_connector_call" or "llm_call" or "human_input" or "secure_input" or "human_approval";

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

        // Accept list-style branches:
        // branches:
        //   - condition: "true"
        //     next: done
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

    private static StepRetryPolicy? MapRetry(RawRetry? r) =>
        r == null ? null : new StepRetryPolicy
        {
            MaxAttempts = r.MaxAttempts ?? 3,
            Backoff = r.Backoff ?? "fixed",
            DelayMs = r.DelayMs ?? 1000,
        };

    private static StepErrorPolicy? MapOnError(RawOnError? e) =>
        e == null ? null : new StepErrorPolicy
        {
            Strategy = e.Strategy ?? "fail",
            FallbackStep = string.IsNullOrWhiteSpace(e.FallbackStep) ? e.Fallback : e.FallbackStep,
            DefaultOutput = e.DefaultOutput,
        };

    private sealed class Raw { public string? Name { get; set; } public string? Description { get; set; } public List<RawRole>? Roles { get; set; } public List<RawStep>? Steps { get; set; } public RawConfiguration? Configuration { get; set; } }
    private sealed class RawRole
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? SystemPrompt { get; set; }
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public double? Temperature { get; set; }
        public int? MaxTokens { get; set; }
        public int? MaxToolRounds { get; set; }
        public int? MaxHistoryMessages { get; set; }
        public int? StreamBufferCapacity { get; set; }
        public string? EventModules { get; set; }
        public string? EventRoutes { get; set; }
        public RawRoleExtensions? Extensions { get; set; }
        public List<string>? Connectors { get; set; }
    }
    private sealed class RawStep
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? TargetRole { get; set; }
        public string? Role { get; set; }
        public object? Workers { get; set; }
        public object? ParallelCount { get; set; }
        public object? Count { get; set; }
        public string? VoteStepType { get; set; }
        public object? Delimiter { get; set; }
        public string? SubStepType { get; set; }
        public string? SubTargetRole { get; set; }
        public string? MapStepType { get; set; }
        public string? MapTargetRole { get; set; }
        public string? ReduceStepType { get; set; }
        public string? ReduceTargetRole { get; set; }
        public string? ReducePromptPrefix { get; set; }
        public string? SignalName { get; set; }
        public string? Prompt { get; set; }
        public object? Timeout { get; set; }
        public object? TimeoutSeconds { get; set; }
        public object? DurationMs { get; set; }
        public string? Variable { get; set; }
        public string? OnTimeout { get; set; }
        public string? OnReject { get; set; }
        public string? Workflow { get; set; }
        public string? Lifecycle { get; set; }
        public string? Query { get; set; }
        public object? TopK { get; set; }
        public object? Facts { get; set; }
        public Dictionary<string, object?>? Parameters { get; set; }
        public string? Next { get; set; }
        public List<RawStep>? Children { get; set; }
        public object? Branches { get; set; }
        public RawRetry? Retry { get; set; }
        public RawOnError? OnError { get; set; }
        public int? TimeoutMs { get; set; }
    }
    private sealed class RawRetry { public int? MaxAttempts { get; set; } public string? Backoff { get; set; } public int? DelayMs { get; set; } }
    private sealed class RawOnError
    {
        public string? Strategy { get; set; }
        public string? FallbackStep { get; set; }
        // Backward-compatible alias for LLM-generated YAML that used on_error.fallback.
        public string? Fallback { get; set; }
        public string? DefaultOutput { get; set; }
    }
    private sealed class RawConfiguration { public bool? ClosedWorldMode { get; set; } }
    private sealed class RawRoleExtensions { public string? EventModules { get; set; } public string? EventRoutes { get; set; } }
}
