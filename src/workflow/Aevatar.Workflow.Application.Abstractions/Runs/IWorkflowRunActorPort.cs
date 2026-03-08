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

public enum WorkflowActorKind
{
    Unsupported = 0,
    Definition = 1,
    Run = 2,
}

public sealed record WorkflowDefinitionBinding(
    string DefinitionActorId,
    string WorkflowName,
    string WorkflowYaml,
    IReadOnlyDictionary<string, string> InlineWorkflowYamls);

public sealed record WorkflowRunCreationResult(
    IActor Actor,
    string DefinitionActorId,
    IReadOnlyList<string> CreatedActorIds);

public sealed record WorkflowActorBinding(
    WorkflowActorKind ActorKind,
    string ActorId,
    string DefinitionActorId,
    string RunId,
    string WorkflowName,
    string WorkflowYaml,
    IReadOnlyDictionary<string, string> InlineWorkflowYamls)
{
    public static WorkflowActorBinding Unsupported(string actorId) =>
        new(
            WorkflowActorKind.Unsupported,
            actorId ?? string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public bool IsWorkflowCapable => ActorKind != WorkflowActorKind.Unsupported;

    public bool HasWorkflowName => !string.IsNullOrWhiteSpace(WorkflowName);

    public bool HasDefinitionPayload =>
        !string.IsNullOrWhiteSpace(WorkflowYaml) || InlineWorkflowYamls.Count > 0;

    public string EffectiveDefinitionActorId =>
        !string.IsNullOrWhiteSpace(DefinitionActorId)
            ? DefinitionActorId
            : ActorKind == WorkflowActorKind.Definition
                ? ActorId
                : string.Empty;
}

/// <summary>
/// Narrow read contract for resolving workflow actor bindings without exposing raw actor state.
/// </summary>
public interface IWorkflowActorBindingReader
{
    Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default);
}

/// <summary>
/// Port for resolving workflow definition actors and creating workflow execution actors.
/// Implemented by infrastructure to avoid Application depending on Workflow.Core.
/// </summary>
public interface IWorkflowRunActorPort
{
    Task<IActor> CreateDefinitionAsync(string? actorId = null, CancellationToken ct = default);

    Task<WorkflowRunCreationResult> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default);

    Task DestroyAsync(string actorId, CancellationToken ct = default);

    Task BindWorkflowDefinitionAsync(
        IActor actor,
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
        CancellationToken ct = default);

    Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default);
}
