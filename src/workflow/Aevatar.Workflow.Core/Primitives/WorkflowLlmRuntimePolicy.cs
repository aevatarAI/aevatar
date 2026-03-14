namespace Aevatar.Workflow.Core.Primitives;

public static class WorkflowLlmRuntimePolicy
{
    private static readonly HashSet<string> AlwaysRequireLlmStepTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "evaluate",
        "reflect",
    };

    public static bool RequiresLlmProvider(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var roles = definition.Roles.ToDictionary(role => role.Id, StringComparer.OrdinalIgnoreCase);
        return EnumerateSteps(definition.Steps).Any(step => StepRequiresLlmProvider(step, roles));
    }

    private static bool StepRequiresLlmProvider(
        StepDefinition step,
        IReadOnlyDictionary<string, RoleDefinition> roles)
    {
        var canonicalType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
        if (AlwaysRequireLlmStepTypes.Contains(canonicalType))
            return true;

        if (string.Equals(canonicalType, "llm_call", StringComparison.OrdinalIgnoreCase))
        {
            var roleId = step.TargetRole ?? string.Empty;
            if (string.IsNullOrWhiteSpace(roleId))
                return true;

            if (!roles.TryGetValue(roleId.Trim(), out var role))
                return true;

            return string.IsNullOrWhiteSpace(role.EventModules);
        }

        return false;
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
