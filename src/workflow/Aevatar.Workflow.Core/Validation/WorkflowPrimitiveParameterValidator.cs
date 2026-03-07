using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Validation;

public sealed class WorkflowPrimitiveParameterValidator
{
    public void Validate(
        WorkflowDefinition workflow,
        IReadOnlyList<StepDefinition> allSteps,
        WorkflowValidationOptions options,
        ISet<string>? availableWorkflowNames,
        List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(allSteps);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(errors);

        var knownCanonicalStepTypes = options.RequireKnownStepTypes
            ? WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(options.KnownStepTypes)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var closedWorldMode = options.ForceClosedWorldMode ?? workflow.Configuration.ClosedWorldMode;

        foreach (var step in allSteps)
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

            ValidateTypeSpecificRules(step, availableWorkflowNames, options, errors);
            if (closedWorldMode)
                ValidateClosedWorldRules(step, errors);
        }
    }

    private static void ValidateTypeSpecificRules(
        StepDefinition step,
        ISet<string>? availableWorkflowNames,
        WorkflowValidationOptions options,
        List<string> errors)
    {
        var stepType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
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

        if (stepType != "workflow_call")
            return;

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
    }

    private static void ValidateClosedWorldRules(StepDefinition step, List<string> errors)
    {
        if (WorkflowPrimitiveCatalog.IsClosedWorldBlocked(step.Type))
            errors.Add($"步骤 '{step.Id}' 使用非闭包原语 '{step.Type}'（closed_world_mode=true）");

        foreach (var (key, value) in step.Parameters)
        {
            if (WorkflowPrimitiveCatalog.IsStepTypeParameterKey(key) &&
                WorkflowPrimitiveCatalog.IsClosedWorldBlocked(value))
            {
                errors.Add($"步骤 '{step.Id}' 的参数 '{key}' 引用了非闭包原语 '{value}'");
            }
        }
    }
}
