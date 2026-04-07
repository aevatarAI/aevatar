using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Core.Primitives;

public sealed class WorkflowYamlValidatorImpl : IWorkflowYamlValidator
{
    private readonly WorkflowParser _parser = new();

    public WorkflowYamlValidationResult Validate(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return new(false, null, null, 0, 0, null, [new("error", "YAML content is empty")]);

        try
        {
            var def = _parser.Parse(yaml);
            return new(
                Success: true,
                NormalizedName: def.Name,
                NormalizedYaml: yaml,
                StepCount: CountSteps(def.Steps),
                RoleCount: def.Roles?.Count ?? 0,
                Description: def.Description,
                Diagnostics: []);
        }
        catch (Exception ex)
        {
            return new(false, null, null, 0, 0, null,
                [new("error", ex.Message)]);
        }
    }

    private static int CountSteps(List<StepDefinition>? steps)
    {
        if (steps is null) return 0;
        int count = 0;
        foreach (var step in steps)
        {
            count++;
            if (step.Children is { Count: > 0 })
                count += CountSteps(step.Children);
        }
        return count;
    }
}
