using Aevatar.GAgentService.Abstractions.Commands;
using System.Text.Json.Serialization;

namespace Aevatar.GAgentService.Abstractions;

public sealed record ScopeWorkflowUpsertRequest(
    string ScopeId,
    string WorkflowId,
    string WorkflowYaml,
    string? WorkflowName = null,
    string? DisplayName = null,
    IReadOnlyDictionary<string, string>? InlineWorkflowYamls = null,
    string? RevisionId = null)
{
    [JsonIgnore]
    public WorkflowCapabilityAdmissionContext? CapabilityAdmission { get; init; }
}

public sealed record ScopeWorkflowSaveAndBindRequest(
    string ScopeId,
    string? WorkflowId,
    string WorkflowYaml,
    string? WorkflowName = null,
    string? DisplayName = null,
    IReadOnlyDictionary<string, string>? InlineWorkflowYamls = null,
    string? AppId = null,
    string? ServiceId = null,
    bool? ExposureDesired = null,
    string? RevisionId = null)
{
    [JsonIgnore]
    public WorkflowCapabilityAdmissionContext? CapabilityAdmission { get; init; }
}

public enum ScopeWorkflowLookupStatus
{
    NotFound = 0,
    NotReady = 1,
    Stale = 2,
    Runnable = 3,
}

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

public sealed record ScopeWorkflowLookupResult(
    ScopeWorkflowLookupStatus Status,
    ScopeWorkflowSummary? Workflow,
    string Reason)
{
    public bool IsRunnable => Status == ScopeWorkflowLookupStatus.Runnable && Workflow != null;
}

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

public sealed record ScopeWorkflowSaveAndBindResult(
    string ScopeId,
    string WorkflowId,
    string RevisionId,
    ScopeWorkflowUpsertResult Workflow,
    ScopeBindingUpsertResult Binding,
    string AcceptanceStage = "accepted",
    string PropagationStage = "readmodel_propagating");
