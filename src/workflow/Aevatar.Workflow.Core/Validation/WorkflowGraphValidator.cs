using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Validation;

public sealed class WorkflowGraphValidator
{
    public void Validate(
        IReadOnlyList<StepDefinition> allSteps,
        List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(allSteps);
        ArgumentNullException.ThrowIfNull(errors);

        var stepIds = allSteps
            .Where(step => !string.IsNullOrWhiteSpace(step.Id))
            .Select(step => step.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var step in allSteps)
        {
            if (!string.IsNullOrWhiteSpace(step.Next) && !stepIds.Contains(step.Next))
                errors.Add($"步骤 '{step.Id}' 的 next 引用不存在的步骤 '{step.Next}'");

            if (step.Branches == null)
                continue;

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
    }
}
