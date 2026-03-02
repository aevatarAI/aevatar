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

/// <summary>
/// Port for resolving, creating, and configuring workflow-capable actors.
/// Implemented by infrastructure to avoid Application depending on Workflow.Core.
/// </summary>
public interface IWorkflowRunActorPort
{
    Task<IActor?> GetAsync(string actorId, CancellationToken ct = default);

    Task<IActor> CreateAsync(CancellationToken ct = default);

    Task DestroyAsync(string actorId, CancellationToken ct = default);

    Task<bool> IsWorkflowActorAsync(IActor actor, CancellationToken ct = default);

    Task<string?> GetBoundWorkflowNameAsync(IActor actor, CancellationToken ct = default);

    Task ConfigureWorkflowAsync(
        IActor actor,
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
        CancellationToken ct = default);

    Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default);
}
