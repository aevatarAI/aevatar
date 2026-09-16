using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public enum WorkflowDeliveryLifecycleStatus
{
    Unspecified = 0,
    Active = 1,
    Revoked = 2,
}

public enum WorkflowDeliveryVariableKind
{
    Unspecified = 0,
    String = 1,
    Integer = 2,
    Number = 3,
    Boolean = 4,
}

public enum WorkflowDeliveryTriggerKind
{
    Unspecified = 0,
    None = 1,
    OneShot = 2,
    Cron = 3,
}

public enum WorkflowInstallationStatus
{
    Unspecified = 0,
    Accepted = 1,
    ProvisioningAccepted = 2,
    Failed = 3,
    Ready = 4,
}

public enum WorkflowScheduleReadinessStatus
{
    Unspecified = 0,
    Ready = 1,
}

public enum WorkflowAcceptanceRunStatus
{
    Unspecified = 0,
    TerminalSuccess = 1,
    TerminalFailure = 2,
}

public enum WorkflowInstallationArtifactKind
{
    Unspecified = 0,
    RunOutput = 1,
    SideEffectReceipt = 2,
    ApprovalResult = 3,
    ExternalResource = 4,
}

public enum WorkflowInstallationArtifactVerificationStatus
{
    Unspecified = 0,
    Verified = 1,
    Rejected = 2,
}

public enum WorkflowDeliveryConnectionStatus
{
    Unspecified = 0,
    Pending = 1,
    Completed = 2,
    Failed = 3,
    Expired = 4,
}

public enum WorkflowDeliveryAcceptanceMode
{
    Unspecified = 0,
    AutomaticPreview = 1,
    Manual = 2,
}

public enum WorkflowDeliveryCommandAckStage
{
    AcceptedForDispatch = 1,
}

public sealed record WorkflowDeliveryVariableDefinition(
    string Key,
    string Label,
    string Description,
    WorkflowDeliveryVariableKind Kind,
    bool Required,
    string YamlPointer,
    string? JsonPointer,
    string? DefaultValue);

public sealed record WorkflowDeliveryConnectionSlotDefinition(
    string Key,
    string Label,
    string ServiceSlug,
    bool Required);

public enum WorkflowDeliveryAcceptanceDateProjection
{
    Unspecified = 0,
    UtcDate = 1,
    UtcYearMonth = 2,
    UtcIsoWeek = 3,
    UtcCompactDate = 4,
}

public abstract record WorkflowDeliveryAcceptanceInputBindingSource;

public sealed record WorkflowDeliveryInstallationCreatedAtUtcInput(
    WorkflowDeliveryAcceptanceDateProjection DateProjection,
    int DayOffset) : WorkflowDeliveryAcceptanceInputBindingSource;

public sealed record WorkflowDeliveryAuthenticatedOwnerExternalUserIdInput
    : WorkflowDeliveryAcceptanceInputBindingSource;

public sealed record WorkflowDeliveryAcceptanceInputBinding(
    string Key,
    string Prefix,
    string Suffix,
    WorkflowDeliveryAcceptanceInputBindingSource Source);

public sealed record WorkflowDeliveryAcceptanceInputRecipe(
    Struct Literals,
    IReadOnlyList<WorkflowDeliveryAcceptanceInputBinding> Bindings);

public sealed record WorkflowDeliveryAcceptancePolicy(
    WorkflowDeliveryAcceptanceMode Mode,
    string? Limitation,
    WorkflowDeliveryAcceptanceInputRecipe Input,
    bool InputDeclared = true);

public sealed record WorkflowDeliveryPackageSnapshot(
    string PackageId,
    string PackageVersionId,
    string WorkflowName,
    string Version,
    string DisplayName,
    string Description,
    string SourceYaml,
    string SourceHash,
    string PackageHash,
    IReadOnlyList<WorkflowDeliveryVariableDefinition> VariableSchema,
    IReadOnlyList<WorkflowDeliveryConnectionSlotDefinition> ConnectionSlots,
    IReadOnlyList<string> Capabilities,
    string RiskSummary,
    IReadOnlyList<string> ParserDiagnostics,
    WorkflowDeliveryAcceptancePolicy AcceptancePolicy,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc);

public sealed record WorkflowDeliveryTriggerIntent(
    WorkflowDeliveryTriggerKind Kind,
    string? Cron,
    string? TimeZone,
    bool RunImmediately);

public sealed record WorkflowDeliveryConfirmationReference(
    string CallSiteId,
    string RequestContractDigest);

public sealed record WorkflowDeliveryConnectionSnapshot(
    string SlotKey,
    string ServiceSlug,
    string LinkId,
    WorkflowDeliveryConnectionStatus Status,
    string? UserServiceId,
    DateTimeOffset UpdatedAtUtc);

public sealed record WorkflowPublishedServiceReadinessEvidence(
    string PublishedServiceId,
    bool Committed,
    bool Runnable,
    long CommittedStateVersion);

public sealed record WorkflowBoundRevisionReadinessEvidence(
    string RevisionId,
    string? BindingRunId,
    bool Bound,
    long CommittedStateVersion);

public sealed record WorkflowNoTriggerReadinessEvidence(bool Ready);

public sealed record WorkflowScheduleReadinessEvidence(
    string ScheduleId,
    string ScheduleProvisioningId,
    WorkflowScheduleReadinessStatus Status,
    long CommittedStateVersion);

public sealed record WorkflowTriggerReadinessEvidence(
    WorkflowDeliveryTriggerIntent Intent,
    WorkflowNoTriggerReadinessEvidence? NoTrigger,
    WorkflowScheduleReadinessEvidence? Schedule);

public sealed record WorkflowAcceptanceRunReadinessEvidence(
    string AcceptanceRunId,
    WorkflowAcceptanceRunStatus Status,
    long CommittedStateVersion);

public sealed record WorkflowInstallationArtifactEvidence(
    WorkflowInstallationArtifactKind Kind,
    string ArtifactId,
    WorkflowInstallationArtifactVerificationStatus VerificationStatus,
    string? VerificationReference,
    string? ContentDigest);

public sealed record WorkflowInstallationReadinessEvidence(
    WorkflowPublishedServiceReadinessEvidence PublishedService,
    WorkflowBoundRevisionReadinessEvidence BoundRevision,
    WorkflowTriggerReadinessEvidence Trigger,
    WorkflowAcceptanceRunReadinessEvidence? AcceptanceRun,
    IReadOnlyList<WorkflowInstallationArtifactEvidence> Artifacts);

public sealed record WorkflowInstallationContinuationClaimSnapshot(
    string ClaimId,
    string ClaimantId,
    WorkflowInstallationStatus ExpectedStatus,
    int Attempt,
    string OperationId,
    DateTimeOffset ClaimedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record WorkflowInstallationSnapshot(
    string InstallationId,
    string IdempotencyKey,
    string ScopeId,
    string TeamId,
    IReadOnlyDictionary<string, string> ConfigurationValues,
    IReadOnlyDictionary<string, string> ConnectionReferences,
    WorkflowDeliveryTriggerIntent TriggerIntent,
    string SourceHash,
    string ResolvedHash,
    string ResolvedYaml,
    IReadOnlyList<WorkflowDeliveryConfirmationReference> Confirmations,
    WorkflowCapabilityAdmissionPlan CapabilityAdmissionPlan,
    AuthenticatedAuthorizationOwnerContext? AuthenticatedOwner,
    Struct? AcceptanceInput,
    string OperationId,
    WorkflowInstallationStatus Status,
    string Stage,
    string? ErrorCode,
    string? ErrorMessage,
    string? WorkflowId,
    string? MemberId,
    string? PublishedServiceId,
    string? RevisionId,
    string? BindingRunId,
    string? ScheduleId,
    string? ScheduleProvisioningId,
    string? ScheduleProvisioningStatus,
    WorkflowInstallationReadinessEvidence? ReadinessEvidence,
    int Attempt,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public WorkflowInstallationContinuationClaimSnapshot? ContinuationClaim { get; init; }

    public string? AcceptanceRunId => ReadinessEvidence?.AcceptanceRun?.AcceptanceRunId;

    public IReadOnlyList<string> ArtifactEvidence =>
        ReadinessEvidence?.Artifacts.Select(static evidence => evidence.ArtifactId).ToArray() ?? [];
}

public sealed record WorkflowDeliverySnapshot(
    string DeliveryId,
    WorkflowDeliveryPackageSnapshot Package,
    string TargetScopeId,
    DateTimeOffset ExpiresAtUtc,
    string? Note,
    WorkflowDeliveryLifecycleStatus LifecycleStatus,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? AccessedAtUtc,
    string? RevokedBy,
    DateTimeOffset? RevokedAtUtc,
    IReadOnlyList<WorkflowDeliveryConnectionSnapshot> Connections,
    WorkflowInstallationSnapshot? Installation,
    long StateVersion,
    DateTimeOffset ProjectedAtUtc);

public sealed record WorkflowDeliveryListQuery(
    string? TargetScopeId = null,
    int? PageSize = null,
    string? PageToken = null,
    WorkflowInstallationStatus? InstallationStatus = null);

public sealed record WorkflowDeliveryListResult(
    IReadOnlyList<WorkflowDeliverySnapshot> Items,
    string? NextPageToken);

public sealed record WorkflowDeliveryCommandReceipt(
    string DeliveryId,
    string ActorId,
    string CommandId,
    string CorrelationId,
    WorkflowDeliveryCommandAckStage AckStage,
    DateTimeOffset? AckedAtUtc);

public sealed record CreateWorkflowDeliveryMutation(
    string DeliveryId,
    WorkflowDeliveryPackageSnapshot Package,
    string TargetScopeId,
    DateTimeOffset ExpiresAtUtc,
    bool ExpiresAtDefaulted,
    string? Note,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc);

public sealed record RecordWorkflowDeliveryAccessMutation(
    string DeliveryId,
    string TargetScopeId,
    DateTimeOffset AccessedAtUtc);

public sealed record RevokeWorkflowDeliveryMutation(
    string DeliveryId,
    string RevokedBy,
    DateTimeOffset RevokedAtUtc);

public sealed record BeginWorkflowDeliveryConnectionMutation(
    string DeliveryId,
    string TargetScopeId,
    string SlotKey,
    string ServiceSlug,
    string LinkId,
    DateTimeOffset RequestedAtUtc);

public sealed record UpdateWorkflowDeliveryConnectionMutation(
    string DeliveryId,
    string TargetScopeId,
    string SlotKey,
    string LinkId,
    WorkflowDeliveryConnectionStatus Status,
    string? UserServiceId,
    DateTimeOffset UpdatedAtUtc);

public sealed record AttachWorkflowDeliveryConnectionMutation(
    string DeliveryId,
    string TargetScopeId,
    string SlotKey,
    string ServiceSlug,
    string UserServiceId,
    DateTimeOffset AttachedAtUtc,
    long ExpectedStateVersion);

public sealed record StartWorkflowInstallationMutation(
    string DeliveryId,
    string InstallationId,
    string IdempotencyKey,
    string ScopeId,
    string TeamId,
    IReadOnlyDictionary<string, string> ConfigurationValues,
    IReadOnlyDictionary<string, string> ConnectionReferences,
    WorkflowDeliveryTriggerIntent TriggerIntent,
    string SourceHash,
    string ResolvedHash,
    string ResolvedYaml,
    IReadOnlyList<WorkflowDeliveryConfirmationReference> Confirmations,
    WorkflowCapabilityAdmissionPlan CapabilityAdmissionPlan,
    AuthenticatedAuthorizationOwnerContext? AuthenticatedOwner,
    string OperationId,
    DateTimeOffset RequestedAtUtc);

public sealed record RetryWorkflowInstallationMutation(
    string DeliveryId,
    string InstallationId,
    string IdempotencyKey,
    string OperationId,
    DateTimeOffset RequestedAtUtc);

public sealed record ClaimWorkflowInstallationContinuationMutation(
    string DeliveryId,
    string InstallationId,
    WorkflowInstallationStatus ExpectedStatus,
    int Attempt,
    string OperationId,
    string ClaimId,
    string ClaimantId,
    TimeSpan RequestedDuration);

public sealed record RecordWorkflowProvisioningAcceptedMutation(
    string DeliveryId,
    string InstallationId,
    string MemberId,
    string? WorkflowId,
    string? PublishedServiceId,
    string? RevisionId,
    string? BindingRunId,
    string? ScheduleId,
    string? ScheduleProvisioningId,
    string? ScheduleProvisioningStatus,
    int Attempt,
    string OperationId,
    DateTimeOffset AcceptedAtUtc,
    string ContinuationClaimId,
    string ContinuationClaimantId);

public sealed record RecordWorkflowInstallationReadyMutation(
    string DeliveryId,
    string InstallationId,
    WorkflowInstallationReadinessEvidence Evidence,
    int Attempt,
    string OperationId,
    DateTimeOffset ReadyAtUtc,
    string ContinuationClaimId,
    string ContinuationClaimantId);

public sealed record RecordWorkflowInstallationFailedMutation(
    string DeliveryId,
    string InstallationId,
    string ErrorCode,
    string? ErrorMessage,
    WorkflowInstallationStatus ExpectedStatus,
    int Attempt,
    string OperationId,
    DateTimeOffset FailedAtUtc,
    string ContinuationClaimId,
    string ContinuationClaimantId);

public interface IWorkflowDeliveryCommandPort
{
    Task<WorkflowDeliveryCommandReceipt> CreateAsync(
        CreateWorkflowDeliveryMutation mutation,
        CancellationToken ct = default);

    Task<WorkflowDeliveryCommandReceipt> RecordAccessAsync(
        RecordWorkflowDeliveryAccessMutation mutation,
        CancellationToken ct = default);

    Task<WorkflowDeliveryCommandReceipt> RevokeAsync(
        RevokeWorkflowDeliveryMutation mutation,
        CancellationToken ct = default);

    Task<WorkflowDeliveryCommandReceipt> BeginConnectionAsync(
        BeginWorkflowDeliveryConnectionMutation mutation,
        CancellationToken ct = default);

    Task<WorkflowDeliveryCommandReceipt> UpdateConnectionAsync(
        UpdateWorkflowDeliveryConnectionMutation mutation,
        CancellationToken ct = default);

    Task<WorkflowDeliveryCommandReceipt> AttachConnectionAsync(
        AttachWorkflowDeliveryConnectionMutation mutation,
        CancellationToken ct = default);

    Task<WorkflowDeliveryCommandReceipt> StartInstallationAsync(
        StartWorkflowInstallationMutation mutation,
        CancellationToken ct = default);

    Task<WorkflowDeliveryCommandReceipt> RetryInstallationAsync(
        RetryWorkflowInstallationMutation mutation,
        CancellationToken ct = default);

    Task<WorkflowDeliveryCommandReceipt> ClaimInstallationContinuationAsync(
        ClaimWorkflowInstallationContinuationMutation mutation,
        CancellationToken ct = default);

    Task<WorkflowDeliveryCommandReceipt> RecordProvisioningAcceptedAsync(
        RecordWorkflowProvisioningAcceptedMutation mutation,
        CancellationToken ct = default);

    Task<WorkflowDeliveryCommandReceipt> RecordInstallationReadyAsync(
        RecordWorkflowInstallationReadyMutation mutation,
        CancellationToken ct = default);

    Task<WorkflowDeliveryCommandReceipt> RecordInstallationFailedAsync(
        RecordWorkflowInstallationFailedMutation mutation,
        CancellationToken ct = default);
}

/// <summary>
/// Reads actor-scoped delivery replicas only. It never activates or replays a delivery actor.
/// </summary>
public interface IWorkflowDeliveryQueryPort
{
    Task<WorkflowDeliverySnapshot?> GetAsync(
        string deliveryId,
        CancellationToken ct = default);

    Task<WorkflowDeliverySnapshot?> GetForScopeAsync(
        string deliveryId,
        string targetScopeId,
        CancellationToken ct = default);

    Task<WorkflowDeliveryListResult> ListAsync(
        WorkflowDeliveryListQuery query,
        CancellationToken ct = default);

    Task<WorkflowDeliverySnapshot?> FindByInstallationAsync(
        string scopeId,
        string installationId,
        CancellationToken ct = default);
}
