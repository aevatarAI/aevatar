namespace Aevatar.Workflow.Application.Abstractions.Workflows;

/// <summary>
/// Validates workflow YAML and returns structured diagnostics.
/// </summary>
public interface IWorkflowYamlValidator
{
    WorkflowYamlValidationResult Validate(string yaml);
}

public sealed record WorkflowYamlValidationResult(
    bool Success,
    string? NormalizedName,
    string? NormalizedYaml,
    int StepCount,
    int RoleCount,
    string? Description,
    IReadOnlyList<WorkflowYamlDiagnostic> Diagnostics);

public sealed record WorkflowYamlDiagnostic(
    string Severity,
    string Message,
    string? StepId = null,
    string? Field = null);
