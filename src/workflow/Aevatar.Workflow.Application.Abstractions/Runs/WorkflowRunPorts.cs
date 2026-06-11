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
    IReadOnlyDictionary<string, string> InlineWorkflowYamls,
    string ScopeId = "");

public sealed record WorkflowRunCreationReceipt(
    string ActorId,
    string DefinitionActorId,
    IReadOnlyList<string> CreatedActorIds);

public sealed record WorkflowDefinitionProvisioningReceipt(
    string ActorId,
    bool CreatedNow);

public sealed record WorkflowActorBinding(
    WorkflowActorKind ActorKind,
    string ActorId,
    string DefinitionActorId,
    string RunId,
    string WorkflowName,
    string WorkflowYaml,
    IReadOnlyDictionary<string, string> InlineWorkflowYamls,
    string ScopeId = "",
    long SourceVersion = 0,
    string SourceEventId = "",
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null)
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

public sealed record WorkflowRunBindingQuery(
    string ScopeId,
    IReadOnlyList<string> DefinitionActorIds,
    int Take = 50);

/// <summary>
/// Narrow read contract for resolving workflow actor bindings without exposing raw actor state.
/// </summary>
public interface IWorkflowActorBindingReader
{
    Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default);
}

/// <summary>
/// Narrow read contract for resolving workflow run bindings by stable run id.
/// </summary>
public interface IWorkflowRunBindingReader
{
    Task<IReadOnlyList<WorkflowActorBinding>> ListByRunIdAsync(
        string runId,
        int take = 20,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorBinding>> QueryAsync(
        WorkflowRunBindingQuery query,
        CancellationToken ct = default);
}

public interface IWorkflowDefinitionProvisioningPort
{
    Task<WorkflowDefinitionProvisioningReceipt> EnsureDefinitionAsync(
        WorkflowDefinitionBinding definition,
        string? preferredActorId = null,
        CancellationToken ct = default);

    Task DestroyAsync(string actorId, CancellationToken ct = default);

    Task BindWorkflowDefinitionAsync(
        string actorId,
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
        string? scopeId = null,
        CancellationToken ct = default);
}

public interface IWorkflowRunProvisioningPort
{
    Task<WorkflowRunCreationReceipt> CreateRunAsync(
        WorkflowDefinitionBinding definition,
        CancellationToken ct = default);

    Task DestroyAsync(string actorId, CancellationToken ct = default);
}

public interface IWorkflowDefinitionParser
{
    /// <summary>
    /// Parses and validates workflow YAML, returning the validated workflow name declared by the YAML.
    /// </summary>
    Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
        string workflowYaml,
        CancellationToken ct = default);
}
