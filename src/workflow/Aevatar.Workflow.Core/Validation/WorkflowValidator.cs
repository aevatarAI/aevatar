using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Validation;

/// <summary>
/// Workflow validator facade. Rule implementation lives in dedicated validators.
/// </summary>
public static class WorkflowValidator
{
    private static readonly WorkflowValidationService DefaultService = new(
        new WorkflowDefinitionStaticValidator(),
        new WorkflowGraphValidator(),
        new WorkflowPrimitiveParameterValidator());

    public static List<string> Validate(WorkflowDefinition workflow) =>
        Validate(workflow, options: null, availableWorkflowNames: null);

    public static List<string> Validate(
        WorkflowDefinition workflow,
        WorkflowValidationOptions? options,
        ISet<string>? availableWorkflowNames) =>
        DefaultService.Validate(workflow, options, availableWorkflowNames);
}
