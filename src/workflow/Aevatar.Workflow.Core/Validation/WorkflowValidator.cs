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
    private static readonly HashSet<string> ClosedWorldBlockedStepTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "llm_call",
        "tool_call",
        "connector_call",
        "bridge_call",
        "evaluate",
        "judge",
        "reflect",
        "human_input",
        "human_approval",
        "wait_signal",
        "wait",
        "emit",
        "publish",
        "parallel",
        "parallel_fanout",
        "fan_out",
        "race",
        "select",
        "map_reduce",
        "mapreduce",
        "vote_consensus",
        "vote",
        "foreach",
        "for_each",
    };

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

        var closedWorldMode = options.ForceClosedWorldMode ?? wf.Configuration.ClosedWorldMode;
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
            if (!string.IsNullOrWhiteSpace(step.TargetRole) && !roleIds.Contains(step.TargetRole))
                errors.Add($"步骤 '{step.Id}' 引用不存在的角色 '{step.TargetRole}'");

            if (!string.IsNullOrWhiteSpace(step.Next) && !stepIds.Contains(step.Next))
                errors.Add($"步骤 '{step.Id}' 的 next 引用不存在的步骤 '{step.Next}'");

            ValidateBranchTargets(step, stepIds, errors);
            ValidateTypeSpecificRules(step, availableWorkflowNames, options, errors);

            if (closedWorldMode)
                ValidateClosedWorldRules(step, errors);
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
        WorkflowValidationOptions options,
        List<string> errors)
    {
        var stepType = NormalizeStepType(step.Type);
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

            if (options.RequireResolvableWorkflowCallTargets &&
                availableWorkflowNames != null &&
                !availableWorkflowNames.Contains(workflowName))
            {
                errors.Add($"步骤 '{step.Id}'（workflow_call）引用未注册工作流 '{workflowName}'");
            }
        }
    }

    private static void ValidateClosedWorldRules(StepDefinition step, List<string> errors)
    {
        if (ClosedWorldBlockedStepTypes.Contains(step.Type))
            errors.Add($"步骤 '{step.Id}' 使用非闭包原语 '{step.Type}'（closed_world_mode=true）");

        foreach (var (key, value) in step.Parameters)
        {
            if (key.EndsWith("_step_type", StringComparison.OrdinalIgnoreCase) &&
                ClosedWorldBlockedStepTypes.Contains(value))
            {
                errors.Add($"步骤 '{step.Id}' 的参数 '{key}' 引用了非闭包原语 '{value}'");
            }
        }
    }

    private static string NormalizeStepType(string stepType)
    {
        var normalized = (stepType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "loop" => "while",
            "sub_workflow" => "workflow_call",
            "for_each" => "foreach",
            "parallel_fanout" or "fan_out" => "parallel",
            "mapreduce" => "map_reduce",
            "judge" => "evaluate",
            "select" => "race",
            _ => normalized,
        };
    }

    public sealed class WorkflowValidationOptions
    {
        public static WorkflowValidationOptions Default { get; } = new();

        /// <summary>
        /// 强制覆盖 workflow 自身的 closed world 配置。null 表示使用 workflow 配置值。
        /// </summary>
        public bool? ForceClosedWorldMode { get; init; }

        /// <summary>
        /// 当提供 availableWorkflowNames 时，是否强制校验 workflow_call 目标可解析。
        /// </summary>
        public bool RequireResolvableWorkflowCallTargets { get; init; } = true;
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
