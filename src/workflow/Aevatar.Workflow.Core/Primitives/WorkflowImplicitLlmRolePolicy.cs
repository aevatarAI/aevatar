namespace Aevatar.Workflow.Core.Primitives;

public static class WorkflowImplicitLlmRolePolicy
{
    public const string DefaultRoleId = "assistant";
    public const string DefaultRoleName = "Assistant";

    public static string ResolveEffectiveTargetRole(
        WorkflowDefinition? workflow,
        StepDefinition step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return ResolveEffectiveTargetRole(
            workflow,
            step.TargetRole,
            step.Type);
    }

    public static string ResolveEffectiveTargetRole(
        WorkflowDefinition? workflow,
        string? configuredTargetRole,
        string? stepType)
    {
        if (!string.IsNullOrWhiteSpace(configuredTargetRole))
            return configuredTargetRole.Trim();

        if (!RequiresImplicitRole(stepType))
            return string.Empty;

        var explicitDefaultRole = FindExplicitDefaultRole(workflow);
        return explicitDefaultRole?.Id?.Trim() ?? DefaultRoleId;
    }

    public static IReadOnlyList<RoleDefinition> GetEffectiveRoles(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var roles = workflow.Roles.ToList();
        if (TryCreateImplicitRole(workflow, out var implicitRole))
            roles.Add(implicitRole);

        return roles;
    }

    private static bool TryCreateImplicitRole(
        WorkflowDefinition workflow,
        out RoleDefinition implicitRole)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        if (FindExplicitDefaultRole(workflow) != null ||
            !EnumerateSteps(workflow.Steps).Any(step => RequiresImplicitRole(step.Type) &&
                                                        string.IsNullOrWhiteSpace(step.TargetRole)))
        {
            implicitRole = null!;
            return false;
        }

        implicitRole = new RoleDefinition
        {
            Id = DefaultRoleId,
            Name = DefaultRoleName,
        };
        return true;
    }

    private static RoleDefinition? FindExplicitDefaultRole(WorkflowDefinition? workflow)
    {
        if (workflow == null)
            return null;

        return workflow.Roles.FirstOrDefault(role =>
            !string.IsNullOrWhiteSpace(role.Id) &&
            string.Equals(role.Id.Trim(), DefaultRoleId, StringComparison.OrdinalIgnoreCase));
    }

    // Refactor (iter30/cluster-030-workflow-step-raw-actor-lifecycle):
    //   Old pattern: WorkflowStepTargetAgentResolver 用 agent_type/agent_id 通过 Type.GetType + AppDomain scan + IRoleAgentTypeResolver 直接 create/link actors,workflow step parameter 暴露 raw CLR lifecycle
    //   New principle: role-level agent_kind 配合 WorkflowRunGAgent runtime lifecycle;step 只用 target_role;删 agent_type/agent_id raw lifecycle 参数 + IWorkflowAgentTypeAliasProvider;Foundation 加 CreateByKindAsync;Bridge 注册 stable kind token
    private static bool RequiresImplicitRole(string? stepType)
    {
        if (!string.Equals(
                WorkflowPrimitiveCatalog.ToCanonicalType(stepType),
                "llm_call",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static IEnumerable<StepDefinition> EnumerateSteps(IEnumerable<StepDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step;

            if (step.Children is not { Count: > 0 })
                continue;

            foreach (var child in EnumerateSteps(step.Children))
                yield return child;
        }
    }
}
