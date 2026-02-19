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
    public static List<string> Validate(WorkflowDefinition wf)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(wf.Name)) errors.Add("缺少 name");
        if (wf.Steps.Count == 0) errors.Add("至少需要一个 step");

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

        return errors;
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
