namespace Aevatar.Workflow.Core;

/// <summary>
/// Immutable snapshot of workflow definition content for bootstrapping execution agents.
/// </summary>
public sealed record WorkflowDefinitionSnapshot(
    string WorkflowYaml,
    string WorkflowName);
