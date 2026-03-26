namespace Aevatar.GAgentService.Abstractions;

public sealed record ScopeWorkflowUpsertRequest(
    string ScopeId,
    string WorkflowId,
    string WorkflowYaml,
    string? WorkflowName = null,
    string? DisplayName = null,
    IReadOnlyDictionary<string, string>? InlineWorkflowYamls = null,
    string? RevisionId = null,
    string? AppId = null);

public sealed record ScopeWorkflowSummary(
    string ScopeId,
    string AppId,
    string WorkflowId,
    string DisplayName,
    string ServiceKey,
    string WorkflowName,
    string ActorId,
    string ActiveRevisionId,
    string DeploymentId,
    string DeploymentStatus,
    DateTimeOffset UpdatedAt);

public sealed record ScopeWorkflowSource(
    string WorkflowYaml,
    string DefinitionActorId,
    IReadOnlyDictionary<string, string>? InlineWorkflowYamls = null);

public sealed record ScopeWorkflowDetail(
    bool Available,
    string ScopeId,
    string AppId,
    ScopeWorkflowSummary? Workflow,
    ScopeWorkflowSource? Source);

public sealed record ScopeWorkflowUpsertResult(
    ScopeWorkflowSummary Workflow,
    string RevisionId,
    string DefinitionActorIdPrefix,
    string ExpectedActorId);
