using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.AI.ToolProviders.Workflow.Ports;

public sealed record WorkflowDefinitionCommandResult(
    bool Success,
    string Name,
    string? RevisionId,
    string? Yaml,
    IReadOnlyList<WorkflowYamlDiagnostic> Diagnostics);
