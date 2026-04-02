using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.AI.ToolProviders.Workflow.Ports;

public sealed record WorkflowDefinitionSummary(
    string Name,
    string? Description,
    int StepCount,
    int RoleCount,
    string RevisionId);

public sealed record WorkflowDefinitionSnapshot(
    string Name,
    string Yaml,
    string RevisionId,
    DateTimeOffset LastModified);

public sealed record WorkflowDefinitionCommandResult(
    bool Success,
    string Name,
    string? RevisionId,
    string? Yaml,
    IReadOnlyList<WorkflowYamlDiagnostic> Diagnostics);
