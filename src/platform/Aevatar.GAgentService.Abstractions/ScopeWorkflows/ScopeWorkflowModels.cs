using Aevatar.GAgentService.Abstractions.Commands;

namespace Aevatar.GAgentService.Abstractions;

public sealed record ScopeWorkflowUpsertRequest(
    string ScopeId,
    string WorkflowId,
    string WorkflowYaml,
    string? WorkflowName = null,
    string? DisplayName = null,
    IReadOnlyDictionary<string, string>? InlineWorkflowYamls = null,
    string? RevisionId = null);

public sealed record ScopeWorkflowSummary(
    string ScopeId,
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
    ScopeWorkflowSummary? Workflow,
    ScopeWorkflowSource? Source);

public sealed record ScopeWorkflowCommandAcceptedHandle(
    string Stage,
    string TargetActorId,
    string CommandId,
    string CorrelationId)
{
    public static ScopeWorkflowCommandAcceptedHandle FromReceipt(
        string stage,
        ServiceCommandAcceptedReceipt receipt) =>
        new(stage, receipt.TargetActorId, receipt.CommandId, receipt.CorrelationId);
}

public sealed record ScopeWorkflowUpsertResult(
    string ScopeId,
    string WorkflowId,
    string ServiceKey,
    string RevisionId,
    string DefinitionActorIdPrefix,
    string ExpectedActorId,
    string ExpectedDeploymentId,
    DateTimeOffset AcceptedAtUtc,
    IReadOnlyList<ScopeWorkflowCommandAcceptedHandle> CommandHandles,
    string ReadModelUrl,
    string AcceptanceStage = "accepted",
    string PropagationStage = "readmodel_propagating",
    string DisplayName = "",
    string WorkflowName = "");
