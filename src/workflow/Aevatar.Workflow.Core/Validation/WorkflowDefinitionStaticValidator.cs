using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Validation;

public sealed class WorkflowDefinitionStaticValidator
{
    public void Validate(
        WorkflowDefinition workflow,
        IReadOnlyList<StepDefinition> allSteps,
        List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(allSteps);
        ArgumentNullException.ThrowIfNull(errors);

        if (string.IsNullOrWhiteSpace(workflow.Name))
            errors.Add("缺少 name");
        if (workflow.Steps.Count == 0)
            errors.Add("至少需要一个 step");

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

        var roleIds = workflow.Roles.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var step in allSteps)
        {
            if (!string.IsNullOrWhiteSpace(step.TargetRole) && !roleIds.Contains(step.TargetRole))
                errors.Add($"步骤 '{step.Id}' 引用不存在的角色 '{step.TargetRole}'");
        }
    }
}
