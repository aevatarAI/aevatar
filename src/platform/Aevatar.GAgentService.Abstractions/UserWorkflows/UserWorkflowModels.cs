namespace Aevatar.GAgentService.Abstractions;

public sealed record UserWorkflowUpsertRequest(
    string UserId,
    string WorkflowId,
    string WorkflowYaml,
    string? WorkflowName = null,
    string? DisplayName = null,
    IReadOnlyDictionary<string, string>? InlineWorkflowYamls = null,
    string? RevisionId = null);

public sealed record UserWorkflowSummary(
    string UserId,
    string WorkflowId,
    string DisplayName,
    string ServiceKey,
    string WorkflowName,
    string ActorId,
    string ActiveRevisionId,
    string DeploymentId,
    string DeploymentStatus,
    DateTimeOffset UpdatedAt);

public sealed record UserWorkflowUpsertResult(
    UserWorkflowSummary Workflow,
    string RevisionId,
    string DefinitionActorIdPrefix,
    string ExpectedActorId);
