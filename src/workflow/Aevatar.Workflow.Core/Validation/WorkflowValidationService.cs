using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Validation;

public sealed class WorkflowValidationService
{
    private readonly WorkflowDefinitionStaticValidator _definitionValidator;
    private readonly WorkflowGraphValidator _graphValidator;
    private readonly WorkflowPrimitiveParameterValidator _primitiveParameterValidator;

    public WorkflowValidationService(
        WorkflowDefinitionStaticValidator definitionValidator,
        WorkflowGraphValidator graphValidator,
        WorkflowPrimitiveParameterValidator primitiveParameterValidator)
    {
        _definitionValidator = definitionValidator ?? throw new ArgumentNullException(nameof(definitionValidator));
        _graphValidator = graphValidator ?? throw new ArgumentNullException(nameof(graphValidator));
        _primitiveParameterValidator = primitiveParameterValidator ?? throw new ArgumentNullException(nameof(primitiveParameterValidator));
    }

    public List<string> Validate(
        WorkflowDefinition workflow,
        WorkflowValidationOptions? options,
        ISet<string>? availableWorkflowNames)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        options ??= WorkflowValidationOptions.Default;
        var errors = new List<string>();
        var allSteps = WorkflowValidationStepEnumerator.Enumerate(workflow.Steps).ToList();

        _definitionValidator.Validate(workflow, allSteps, errors);
        _graphValidator.Validate(allSteps, errors);
        _primitiveParameterValidator.Validate(workflow, allSteps, options, availableWorkflowNames, errors);
        return errors;
    }
}
