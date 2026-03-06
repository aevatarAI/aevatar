using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Application.Abstractions.Runs;

public sealed record WorkflowYamlParseResult(
    string WorkflowName,
    string Error)
{
    public bool Succeeded => string.IsNullOrWhiteSpace(Error);

    public static WorkflowYamlParseResult Success(string workflowName) =>
        new(workflowName ?? string.Empty, string.Empty);

    public static WorkflowYamlParseResult Invalid(string error) =>
        new(string.Empty, error ?? "Workflow YAML is invalid.");
}

public sealed record WorkflowDefinitionBindingSnapshot(
    string WorkflowName,
    string WorkflowYaml,
    IReadOnlyDictionary<string, string> InlineWorkflowYamls)
{
    public bool IsBound =>
        !string.IsNullOrWhiteSpace(WorkflowName) &&
        !string.IsNullOrWhiteSpace(WorkflowYaml);
}

/// <summary>
/// Port for resolving, creating, and binding workflow definitions for workflow-capable actors.
/// Implemented by infrastructure to avoid Application depending on Workflow.Core.
/// </summary>
public interface IWorkflowRunActorPort
{
    Task<IActor?> GetDefinitionActorAsync(string definitionActorId, CancellationToken ct = default);

    Task<IActor?> GetRunActorAsync(string runActorId, CancellationToken ct = default);

    Task<IActor> CreateRunActorAsync(CancellationToken ct = default);

    Task DestroyRunActorAsync(string runActorId, CancellationToken ct = default);

    Task<bool> IsWorkflowDefinitionActorAsync(IActor actor, CancellationToken ct = default);

    Task<bool> IsWorkflowRunActorAsync(IActor actor, CancellationToken ct = default);

    Task<WorkflowDefinitionBindingSnapshot?> GetDefinitionBindingSnapshotAsync(IActor actor, CancellationToken ct = default);

    Task BindWorkflowDefinitionAsync(
        IActor runActor,
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
        CancellationToken ct = default);

    Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default);
}
