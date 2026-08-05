using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Application.Abstractions.Runs;

public enum WorkflowYamlParseErrorCode
{
    None = 0,
    InvalidYaml = 1,
    ResourceLimit = 2,
}

public sealed record WorkflowYamlParseResult(
    string WorkflowName,
    string Error,
    WorkflowAuthorizationDependencies? AuthorizationDependencies = null,
    ExternalCapabilityReadiness? ExternalCapabilityReadiness = null,
    WorkflowYamlParseErrorCode ErrorCode = WorkflowYamlParseErrorCode.None)
{
    public bool Succeeded => string.IsNullOrWhiteSpace(Error);

    public static WorkflowYamlParseResult Success(
        string workflowName,
        WorkflowAuthorizationDependencies? authorizationDependencies = null) =>
        new(workflowName ?? string.Empty, string.Empty, authorizationDependencies?.Clone());

    public static WorkflowYamlParseResult Invalid(
        string error,
        ExternalCapabilityReadiness? externalCapabilityReadiness = null,
        WorkflowYamlParseErrorCode errorCode = WorkflowYamlParseErrorCode.InvalidYaml) =>
        new(
            string.Empty,
            error ?? "Workflow YAML is invalid.",
            null,
            externalCapabilityReadiness?.Clone(),
            errorCode);
}

public sealed record WorkflowInlineYamlBundleParseResult(
    string EntryWorkflowName,
    string EntryWorkflowYaml,
    IReadOnlyDictionary<string, string> WorkflowYamlsByName,
    string Error,
    ExternalCapabilityReadiness? ExternalCapabilityReadiness = null,
    WorkflowYamlParseErrorCode ErrorCode = WorkflowYamlParseErrorCode.None)
{
    public bool Succeeded => string.IsNullOrWhiteSpace(Error);

    public static WorkflowInlineYamlBundleParseResult Success(
        string entryWorkflowName,
        string entryWorkflowYaml,
        IReadOnlyDictionary<string, string> workflowYamlsByName) =>
        new(
            entryWorkflowName ?? string.Empty,
            entryWorkflowYaml ?? string.Empty,
            workflowYamlsByName ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            string.Empty);

    public static WorkflowInlineYamlBundleParseResult Invalid(
        string error,
        ExternalCapabilityReadiness? externalCapabilityReadiness = null,
        WorkflowYamlParseErrorCode errorCode = WorkflowYamlParseErrorCode.InvalidYaml) =>
        new(
            string.Empty,
            string.Empty,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            string.IsNullOrWhiteSpace(error) ? "Workflow YAML is invalid." : error,
            externalCapabilityReadiness?.Clone(),
            errorCode);
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
    ExternalCapabilityExecutionMode ExpectedExecutionMode,
    string ScopeId = "",
    string RunOrigin = "",
    string ScheduleId = "",
    string SourceKind = "",
    WorkflowCapabilityAdmissionPlan? CapabilityAdmissionPlan = null,
    string WorkflowId = "",
    string RevisionId = "");

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
    ExternalCapabilityExecutionMode ExpectedExecutionMode,
    string ScopeId = "",
    long SourceVersion = 0,
    string SourceEventId = "",
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    string SourceKind = "",
    WorkflowCapabilityAdmissionPlan? CapabilityAdmissionPlan = null,
    string WorkflowId = "",
    string RevisionId = "")
{
    public static WorkflowActorBinding Unsupported(string actorId) =>
        new(
            WorkflowActorKind.Unsupported,
            actorId ?? string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ExternalCapabilityExecutionMode.Unspecified);

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
    int Take = 50,
    IReadOnlyList<string>? RunIds = null);

public sealed record WorkflowRunForkSeedView(
    string SourceRunId,
    string Status,
    string WorkflowYaml,
    IReadOnlyDictionary<string, string> InlineWorkflowYamls,
    ExternalCapabilityExecutionMode ExpectedExecutionMode,
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyList<string> CompletedStepIds,
    string LastFailedStepId,
    string FinalError,
    string ScopeId = "",
    IReadOnlyDictionary<string, WorkflowStepIdempotencyView>? IdempotencyByStepId = null,
    WorkflowCapabilityAdmissionPlan? CapabilityAdmissionPlan = null)
{
    public WorkflowRunForkSeedView()
        : this(
            string.Empty,
            string.Empty,
            string.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ExternalCapabilityExecutionMode.Unspecified,
            new Dictionary<string, string>(StringComparer.Ordinal),
            [],
            string.Empty,
            string.Empty,
            string.Empty,
            new Dictionary<string, WorkflowStepIdempotencyView>(StringComparer.Ordinal),
            null)
    {
    }
}

public sealed record WorkflowStepIdempotencyView(
    string LogicalRunId,
    string StepId,
    int LogicalAttempt,
    string IdempotencyKey)
{
    public WorkflowStepIdempotencyView()
        : this(string.Empty, string.Empty, 0, string.Empty)
    {
    }
}

public sealed record WorkflowExternalApprovalContinuation(
    string ActorId,
    string RunId,
    string StepId,
    string SignalName,
    string SourceId,
    string ExternalIdKind,
    string ExternalId,
    string CallbackIdempotencyKey,
    string RequestId,
    long SourceVersion,
    string SourceEventId,
    DateTimeOffset UpdatedAt);

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

public interface IWorkflowRunForkSeedQueryPort
{
    Task<WorkflowRunForkSeedView?> GetForkSeedAsync(
        string scopeId,
        string runId,
        CancellationToken ct = default);
}

public interface IWorkflowExternalApprovalContinuationLookupPort
{
    Task<WorkflowExternalApprovalContinuation?> FindActiveAsync(
        string sourceId,
        string externalIdKind,
        string externalId,
        CancellationToken ct = default);
}

public interface IWorkflowWebhookReplayAdmissionPort
{
    bool IsAvailable { get; }

    ValueTask<WorkflowWebhookReplayAdmission> AdmitAsync(
        WorkflowWebhookReplayAdmissionRequest request,
        CancellationToken ct = default);

    ValueTask ReleaseAsync(
        WorkflowWebhookReplayAdmissionRequest request,
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
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        string? scopeId,
        string? sourceKind,
        WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan,
        string? workflowId,
        string? revisionId,
        ExternalCapabilityExecutionMode expectedExecutionMode,
        CancellationToken ct = default);
}

public interface IWorkflowRunProvisioningPort
{
    Task<WorkflowRunCreationReceipt> CreateRunAsync(
        WorkflowDefinitionBinding definition,
        CancellationToken ct = default);

    Task DestroyAsync(string actorId, CancellationToken ct = default);
}

/// <summary>
/// Narrow capability for provisioning a workflow Run at a caller-supplied
/// stable identity. Kept separate from random Run creation so callers cannot
/// accidentally imply exact-id semantics through the general provisioning port.
/// </summary>
public interface IWorkflowRunIdentityProvisioningPort
{
    Task<WorkflowRunCreationReceipt> EnsureRunAsync(
        WorkflowDefinitionBinding definition,
        string requestedRunId,
        CancellationToken ct = default);
}

/// <summary>
/// Narrow capability for atomically validating an exact Run binding and
/// executing its first command in the same actor turn.
/// </summary>
public interface IWorkflowRunIdentityExecutionPort
{
    Task<WorkflowRunCreationReceipt> EnsureRunAndDispatchAsync(
        WorkflowDefinitionBinding definition,
        string requestedRunId,
        WorkflowChatRequestEvent executionRequest,
        string commandId,
        string correlationId,
        CancellationToken ct = default);
}

public interface IWorkflowDefinitionParser
{
    /// <summary>
    /// Parses and validates workflow YAML, returning the validated workflow name declared by the YAML.
    /// </summary>
    Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
        string workflowYaml,
        CancellationToken ct = default);

    Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
        IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
        CancellationToken ct = default);
}
