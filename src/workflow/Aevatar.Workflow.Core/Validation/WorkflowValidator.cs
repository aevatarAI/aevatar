// ─────────────────────────────────────────────────────────────
// WorkflowValidator — 工作流校验器
// 校验工作流定义的完整性（名称、步骤、角色引用、后继引用）
// ─────────────────────────────────────────────────────────────

using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Validation;

/// <summary>
/// 工作流校验器。对工作流定义进行静态校验，返回错误信息列表。
/// </summary>
public static class WorkflowValidator
{
    /// <summary>
    /// 校验工作流定义。
    /// 检查 name 非空、至少一个步骤、步骤 ID 不重复、角色引用有效、next 引用有效。
    /// </summary>
    /// <param name="wf">待校验的工作流定义。</param>
    /// <returns>错误消息列表，无错误时返回空列表。</returns>
    public static List<string> Validate(WorkflowDefinition wf) =>
        Validate(wf, options: null, availableWorkflowNames: null);

    /// <summary>
    /// 校验工作流定义（可选开启额外闭包规则与跨 workflow 解析校验）。
    /// </summary>
    public static List<string> Validate(
        WorkflowDefinition wf,
        WorkflowValidationOptions? options,
        ISet<string>? availableWorkflowNames)
    {
        options ??= WorkflowValidationOptions.Default;
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(wf.Name)) errors.Add("缺少 name");
        if (wf.Steps.Count == 0) errors.Add("至少需要一个 step");

        var knownCanonicalStepTypes = options.RequireKnownStepTypes
            ? WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(options.KnownStepTypes)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allSteps = EnumerateSteps(wf.Steps).ToList();
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in allSteps)
        {
            if (string.IsNullOrWhiteSpace(step.Id))
            {
                errors.Add("存在缺少 id 的步骤");
                continue;
            }

            if (!stepIds.Add(step.Id))
                errors.Add($"步骤 '{step.Id}' 重复");
        }

        var roleIds = wf.Roles.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var step in allSteps)
        {
            var hasAgentTypeOverride = HasAgentTypeOverride(step);
            if (!hasAgentTypeOverride &&
                !string.IsNullOrWhiteSpace(step.TargetRole) &&
                !roleIds.Contains(step.TargetRole))
            {
                errors.Add($"步骤 '{step.Id}' 引用不存在的角色 '{step.TargetRole}'");
            }

            if (!string.IsNullOrWhiteSpace(step.Next) && !stepIds.Contains(step.Next))
                errors.Add($"步骤 '{step.Id}' 的 next 引用不存在的步骤 '{step.Next}'");

            ValidateBranchTargets(step, stepIds, errors);
            ValidateTypeSpecificRules(step, availableWorkflowNames, knownCanonicalStepTypes, options, errors);

        }

        return errors;
    }

    private static void ValidateBranchTargets(
        StepDefinition step,
        ISet<string> stepIds,
        List<string> errors)
    {
        if (step.Branches == null)
            return;

        foreach (var branch in step.Branches)
        {
            if (string.IsNullOrWhiteSpace(branch.Value))
            {
                errors.Add($"步骤 '{step.Id}' 的分支 '{branch.Key}' 缺少目标步骤");
                continue;
            }

            if (!stepIds.Contains(branch.Value))
                errors.Add($"步骤 '{step.Id}' 的分支 '{branch.Key}' 引用不存在的步骤 '{branch.Value}'");
        }
    }

    private static void ValidateTypeSpecificRules(
        StepDefinition step,
        ISet<string>? availableWorkflowNames,
        ISet<string> knownCanonicalStepTypes,
        WorkflowValidationOptions options,
        List<string> errors)
    {
        var stepType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
        if (options.RequireKnownStepTypes &&
            !WorkflowPrimitiveCatalog.IsKnownStepType(stepType, knownCanonicalStepTypes))
        {
            errors.Add($"步骤 '{step.Id}' 使用未知原语 '{step.Type}'（canonical='{stepType}'）");
        }

        foreach (var (key, value) in step.Parameters)
        {
            if (!WorkflowPrimitiveCatalog.IsStepTypeParameterKey(key) ||
                string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var parameterStepType = WorkflowPrimitiveCatalog.ToCanonicalType(value);
            if (options.RequireKnownStepTypes &&
                !WorkflowPrimitiveCatalog.IsKnownStepType(parameterStepType, knownCanonicalStepTypes))
            {
                errors.Add(
                    $"步骤 '{step.Id}' 的参数 '{key}' 使用未知原语 '{value}'（canonical='{parameterStepType}'）");
            }
        }

        ValidateAgentTypeParameters(step, errors);

        if (stepType == "conditional")
        {
            if (step.Branches == null || step.Branches.Count == 0)
            {
                errors.Add($"步骤 '{step.Id}'（conditional）必须定义 branches");
            }
            else
            {
                if (!step.Branches.ContainsKey("true"))
                    errors.Add($"步骤 '{step.Id}'（conditional）缺少 true 分支");
                if (!step.Branches.ContainsKey("false"))
                    errors.Add($"步骤 '{step.Id}'（conditional）缺少 false 分支");
            }
            return;
        }

        if (stepType == "switch")
        {
            if (step.Branches == null || step.Branches.Count == 0)
            {
                errors.Add($"步骤 '{step.Id}'（switch）必须定义 branches");
            }
            else if (!step.Branches.ContainsKey("_default"))
            {
                errors.Add($"步骤 '{step.Id}'（switch）必须定义 _default 分支");
            }
            return;
        }

        if (stepType == "while")
        {
            var hasCondition = step.Parameters.TryGetValue("condition", out var condition) &&
                               !string.IsNullOrWhiteSpace(condition);
            var hasMaxIterations = step.Parameters.TryGetValue("max_iterations", out var maxIterationsRaw);
            if (!hasCondition && !hasMaxIterations)
                errors.Add($"步骤 '{step.Id}'（while）至少需要 condition 或 max_iterations");

            if (hasMaxIterations &&
                (!int.TryParse(maxIterationsRaw, out var maxIterations) || maxIterations <= 0))
            {
                errors.Add($"步骤 '{step.Id}'（while）max_iterations 必须是正整数");
            }

            if (step.Parameters.TryGetValue("step", out var subStepType) &&
                string.IsNullOrWhiteSpace(subStepType))
            {
                errors.Add($"步骤 '{step.Id}'（while）step 参数不能为空");
            }
            return;
        }

        if (stepType == "workflow_call")
        {
            var workflowName = step.Parameters.GetValueOrDefault("workflow", "").Trim();
            if (string.IsNullOrEmpty(workflowName))
            {
                errors.Add($"步骤 '{step.Id}'（workflow_call）缺少 workflow 参数");
                return;
            }

            var lifecycle = step.Parameters.GetValueOrDefault("lifecycle", "").Trim();
            if (!WorkflowCallLifecycle.IsSupported(lifecycle))
            {
                errors.Add(
                    $"步骤 '{step.Id}'（workflow_call）lifecycle 仅支持 {WorkflowCallLifecycle.AllowedValuesText}，当前值 '{lifecycle}'");
            }

            if (options.RequireResolvableWorkflowCallTargets &&
                availableWorkflowNames != null &&
                !availableWorkflowNames.Contains(workflowName))
            {
                errors.Add($"步骤 '{step.Id}'（workflow_call）引用未注册工作流 '{workflowName}'");
            }
            return;
        }

        if (stepType == "dynamic_workflow" &&
            options.DisallowDynamicWorkflowStep)
        {
            errors.Add(
                $"步骤 '{step.Id}' 使用了保留原语 'dynamic_workflow'。请改为 workflow_call 或其他业务原语。");
        }
    }

    private static bool HasAgentTypeOverride(StepDefinition step) =>
        TryGetParameter(step.Parameters, "agent_type", out var agentType) &&
        !string.IsNullOrWhiteSpace(agentType);

    private static void ValidateAgentTypeParameters(StepDefinition step, List<string> errors)
    {
        if (TryGetParameter(step.Parameters, "agent_type", out var agentTypeRaw))
        {
            var trimmed = (agentTypeRaw ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                errors.Add($"步骤 '{step.Id}' 的参数 'agent_type' 不能为空");
            }
            else if (!IsLikelyAgentTypeString(trimmed))
            {
                errors.Add($"步骤 '{step.Id}' 的参数 'agent_type' 格式非法：'{trimmed}'");
            }
        }

        if (TryGetParameter(step.Parameters, "agent_id", out var agentIdRaw) &&
            string.IsNullOrWhiteSpace(agentIdRaw))
        {
            errors.Add($"步骤 '{step.Id}' 的参数 'agent_id' 不能为空字符串");
        }
    }

    private static bool IsLikelyAgentTypeString(string value)
    {
        var commaIndex = value.IndexOf(',');
        var typePart = commaIndex >= 0 ? value[..commaIndex].Trim() : value.Trim();
        var assemblyPart = commaIndex >= 0 ? value[(commaIndex + 1)..].Trim() : string.Empty;
        if (typePart.Length == 0)
            return false;
        if (commaIndex >= 0 && assemblyPart.Length == 0)
            return false;

        var first = typePart[0];
        if (!(char.IsLetter(first) || first == '_'))
            return false;

        foreach (var ch in typePart)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '.' or '+' or '`')
                continue;
            return false;
        }

        return !typePart.Contains("..", StringComparison.Ordinal);
    }

    private static bool TryGetParameter(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out string value)
    {
        if (parameters.TryGetValue(key, out value!))
            return true;

        foreach (var (existingKey, existingValue) in parameters)
        {
            if (string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
            {
                value = existingValue;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    public sealed class WorkflowValidationOptions
    {
        public static WorkflowValidationOptions Default { get; } = new();

        /// <summary>
        /// 兼容保留字段；validator 不再基于 closed world 配置阻止 step type。
        /// </summary>
        public bool? ForceClosedWorldMode { get; init; }

        /// <summary>
        /// 当提供 availableWorkflowNames 时，是否强制校验 workflow_call 目标可解析。
        /// </summary>
        public bool RequireResolvableWorkflowCallTargets { get; init; } = true;

        /// <summary>
        /// 当 KnownStepTypes 提供且非空时，是否强制校验 step type（含 step type 参数位）必须可解析。
        /// </summary>
        public bool RequireKnownStepTypes { get; init; }

        /// <summary>
        /// 已注册的原语名称集合（可包含别名，校验时会 canonicalize）。
        /// </summary>
        public ISet<string>? KnownStepTypes { get; init; }

        /// <summary>
        /// 是否禁止在待校验 workflow 中使用 dynamic_workflow（该原语保留给引擎内部重配置流程）。
        /// </summary>
        public bool DisallowDynamicWorkflowStep { get; init; }
    }

    private static IEnumerable<StepDefinition> EnumerateSteps(IEnumerable<StepDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            if (step.Children == null || step.Children.Count == 0)
                continue;

            foreach (var child in EnumerateSteps(step.Children))
                yield return child;
        }
    }
}
