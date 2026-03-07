using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;

namespace Aevatar.Workflow.Core;

internal static class WorkflowYamlValidationSupport
{
    public static List<string> ValidateWorkflowYaml(
        string yaml,
        IReadOnlySet<string> knownStepTypes)
    {
        WorkflowDefinition parsed;
        try
        {
            parsed = new WorkflowParser().Parse(yaml);
        }
        catch (Exception ex)
        {
            return [$"YAML parse failed: {ex.Message}"];
        }

        return WorkflowValidator.Validate(
            parsed,
            new WorkflowValidator.WorkflowValidationOptions
            {
                RequireKnownStepTypes = true,
                KnownStepTypes = new HashSet<string>(knownStepTypes, StringComparer.OrdinalIgnoreCase),
            },
            availableWorkflowNames: null);
    }
}
