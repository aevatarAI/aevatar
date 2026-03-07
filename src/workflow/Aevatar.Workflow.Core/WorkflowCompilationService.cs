using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;

namespace Aevatar.Workflow.Core;

public sealed class WorkflowCompilationService
{
    private readonly WorkflowParser _parser = new();
    private readonly IReadOnlySet<string> _knownStepTypes;

    public WorkflowCompilationService(IReadOnlySet<string> knownStepTypes)
    {
        _knownStepTypes = knownStepTypes ?? throw new ArgumentNullException(nameof(knownStepTypes));
    }

    public WorkflowCompilationResult Compile(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return WorkflowCompilationResult.Invalid("workflow yaml is empty");

        try
        {
            var workflow = _parser.Parse(yaml);
            var errors = WorkflowValidator.Validate(
                workflow,
                new WorkflowValidationOptions
                {
                    RequireKnownStepTypes = true,
                    KnownStepTypes = new HashSet<string>(_knownStepTypes, StringComparer.OrdinalIgnoreCase),
                },
                availableWorkflowNames: null);
            if (errors.Count > 0)
                return WorkflowCompilationResult.Invalid(string.Join("; ", errors));

            return WorkflowCompilationResult.Success(workflow);
        }
        catch (Exception ex)
        {
            return WorkflowCompilationResult.Invalid(ex.Message);
        }
    }
}

public readonly record struct WorkflowCompilationResult(
    bool Compiled,
    string CompilationError,
    WorkflowDefinition? Workflow)
{
    public static WorkflowCompilationResult Success(WorkflowDefinition workflow) =>
        new(true, string.Empty, workflow);

    public static WorkflowCompilationResult Invalid(string error) =>
        new(false, error ?? string.Empty, null);
}
