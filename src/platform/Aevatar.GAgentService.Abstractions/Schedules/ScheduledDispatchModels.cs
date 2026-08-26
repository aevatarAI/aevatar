using System.Text.Json.Serialization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;

namespace Aevatar.GAgentService.Abstractions.Schedules;

public enum ScheduledDispatchTargetKind
{
    Envelope = 0,
    ServiceInvocation = 1,
}

public enum ScheduledDispatchScheduleKind
{
    Generic = 0,
    Workflow = 1,
}

public enum ScheduledDispatchScheduleMode
{
    RecurringCron = 0,
    OneShotAtUtc = 1,
}

public sealed record TeamMemberAutomationOwner(
    string ScopeId,
    string MemberId,
    string TeamId = "");

public static class ScheduledDispatchOwnerKinds
{
    public const string StudioMemberAutomation = "studio_member_automation";
}

public sealed record ScheduledDispatchOwner(
    string Kind,
    string ScopeId,
    string TeamId,
    string MemberId)
{
    public TeamMemberAutomationOwner ToTeamMemberAutomationOwner()
    {
        if (!string.Equals(Kind, ScheduledDispatchOwnerKinds.StudioMemberAutomation, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported scheduled dispatch owner kind '{Kind}'.",
                nameof(Kind));
        }

        if (string.IsNullOrWhiteSpace(ScopeId))
            throw new ArgumentException("Owner scopeId is required.", nameof(ScopeId));
        if (string.IsNullOrWhiteSpace(TeamId))
            throw new ArgumentException("Owner teamId is required.", nameof(TeamId));
        if (string.IsNullOrWhiteSpace(MemberId))
            throw new ArgumentException("Owner memberId is required.", nameof(MemberId));

        return new TeamMemberAutomationOwner(
            ScopeId.Trim(),
            MemberId.Trim(),
            TeamId.Trim());
    }
}

public sealed record TeamAutomationActivationDecision(
    string ScheduleId,
    string DisplayName,
    TeamMemberAutomationOwner Owner,
    ServiceIdentity ServiceIdentity,
    string EndpointId,
    Google.Protobuf.WellKnownTypes.Any Payload,
    ScheduledCallerNyxIdAuthority CallerAuthority,
    ScheduledInvocationAuthorizationFact AuthorizationFact,
    string CronExpression,
    string Timezone,
    bool Enabled,
    ScheduledDispatchScheduleKind ScheduleKind,
    IReadOnlyDictionary<string, string> Headers,
    ScheduledDispatchScheduleMode ScheduleMode,
    DateTimeOffset? OneShotFireAt,
    ScheduledDispatchCredentialRequirementTargetKind CredentialRequirementTargetKind,
    string RevisionId,
    ServiceInvocationCaller? Caller);

public enum TeamAutomationLifecycleStatus
{
    Unspecified = 0,
    ProvisioningPending = 1,
    Active = 2,
    NeedsAuthorization = 3,
    ReplacementPending = 4,
    Deleting = 5,
    RevocationPending = 6,
    Failed = 7,
}

public enum TeamAutomationOperationKind
{
    Create = 1,
    Reauthorize = 2,
    Delete = 3,
}

public sealed record TeamAutomationCredentialOperation(
    string ScheduleId,
    TeamMemberAutomationOwner Owner,
    string OperationId,
    string IdempotencyKey,
    string PermissionDigest,
    string PolicyVersion,
    TeamAutomationOperationKind Kind,
    ScheduledCredentialEffectLocator CredentialEffectLocator,
    TeamAutomationActivationDecision ActivationDecision,
    string MutationDigest);

public sealed record ScheduledCredentialEffectLocator(
    string CredentialName,
    string RequestedSecretReference,
    string SecretPurpose,
    string SecretOwnerScopeKey,
    ScheduledInvocationAuthorizationOwner CredentialOwner);

public sealed record ScheduledDispatchTargetDescriptor(
    ScheduledDispatchTargetKind Kind,
    string? ActorId = null,
    EventEnvelope? Envelope = null,
    ScheduledServiceInvocationTargetDescriptor? ServiceInvocation = null);

public sealed record ScheduledServiceInvocationTargetDescriptor(
    ServiceIdentity Identity,
    string EndpointId,
    Google.Protobuf.WellKnownTypes.Any Payload,
    string? RevisionId = null,
    ServiceInvocationCaller? Caller = null,
    ScheduledServiceInvocationAuth? Auth = null,
    ScheduledInvocationAuthorizationFact? AuthorizationFact = null);

public sealed record ScheduledInvocationAuthorizationFact(
    string PermissionDigest,
    string PolicyVersion,
    ScheduledInvocationAuthorizationOwner Owner,
    IReadOnlyList<ScheduledInvocationAuthorizationServiceGrant> ServiceGrants,
    string Scopes,
    DateTimeOffset ExpiresAt,
    bool ServiceGrantsNotRequired,
    ScheduledInvocationAuthorizationDisclosure Disclosure,
    ScheduledInvocationAuthorizationAuthority Authority,
    ScheduledInvocationOwnerLLMSelection? OwnerLLMSelection = null);

public sealed record ScheduledInvocationAuthorizationOwner(
    string Authority,
    string OwnerKind,
    string OwnerSubject);

public sealed record ScheduledInvocationAuthorizationServiceGrant(
    string ServiceId,
    IReadOnlyList<string> NodeIds,
    bool NodeGrantsNotRequired);

public sealed record ScheduledInvocationAuthorizationDisclosure(
    bool DedicatedToSchedule,
    bool SecretManagedByAevatar,
    bool BrowserReceivesRawKey,
    bool DeleteRevokesCredential,
    bool PauseResumeRevokesCredential);

public sealed record ScheduledInvocationAuthorizationAuthority(
    long MemberStateVersion,
    long WorkflowStateVersion,
    long ConnectorStateVersion,
    long OwnerLlmStateVersion,
    long CatalogStateVersion,
    DateTimeOffset CatalogObservedAt,
    DateTimeOffset CatalogFreshUntil,
    string CatalogContentDigest,
    string CatalogContractVersion,
    string CatalogPolicyVersion,
    DateTimeOffset CatalogEvaluatedAt);

public sealed record ScheduledServiceInvocationNyxIdSubjectRef(
    string Platform,
    string Tenant,
    string ExternalUserId);

public enum ScheduledServiceInvocationNyxIdCredentialRole
{
    Sender = 1,
    ScopeOwner = 2,
}

public abstract record ScheduledServiceInvocationCredentialSource;

public sealed record ScheduledServiceInvocationNyxIdCredentialSource(
    ScheduledServiceInvocationNyxIdSubjectRef Subject,
    string Scope,
    ScheduledServiceInvocationNyxIdCredentialRole Role = ScheduledServiceInvocationNyxIdCredentialRole.Sender)
    : ScheduledServiceInvocationCredentialSource;

public sealed record ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource(
    string Scope,
    ScheduledServiceInvocationNyxIdSubjectRef? OwnerSubject = null);

public sealed record ScheduledInvocationAgentKeyCredentialReference(
    SecretReference SecretReference,
    string ApiKeyId,
    long KeyExpiresAtUnixMs,
    IReadOnlyList<NyxIdDurableOperationGrantRef>? DurableOperationGrants = null)
    : ScheduledServiceInvocationCredentialSource;

public sealed record ScheduledServiceInvocationDurableCredentialReference(
    string CredentialId,
    SecretReference SecretReference)
    : ScheduledServiceInvocationCredentialSource;

public sealed record ScheduledServiceInvocationAuth
{
    public ScheduledServiceInvocationAuth()
    {
    }

    public ScheduledServiceInvocationAuth(ScheduledServiceInvocationCredentialSource source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public ScheduledServiceInvocationAuth(ScheduledServiceInvocationNyxIdCredentialSource SenderNyxId)
        : this((ScheduledServiceInvocationCredentialSource)(SenderNyxId ??
                                                            throw new ArgumentNullException(nameof(SenderNyxId))))
    {
    }

    public ScheduledServiceInvocationAuth(ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource ScopeOwnerNyxId)
        : this((ScheduledServiceInvocationCredentialSource)ToNyxIdSource(ScopeOwnerNyxId))
    {
    }

    public ScheduledServiceInvocationAuth(ScheduledServiceInvocationDurableCredentialReference DurableCredentialReference)
        : this((ScheduledServiceInvocationCredentialSource)(DurableCredentialReference ??
                                                            throw new ArgumentNullException(nameof(DurableCredentialReference))))
    {
    }

    public ScheduledServiceInvocationAuth(ScheduledInvocationAgentKeyCredentialReference ScheduledInvocationAgentKey)
        : this((ScheduledServiceInvocationCredentialSource)(ScheduledInvocationAgentKey ??
                                                            throw new ArgumentNullException(nameof(ScheduledInvocationAgentKey))))
    {
    }

    public ScheduledServiceInvocationCredentialSource? Source { get; init; }

    public ScheduledCallerNyxIdAuthority? CallerAuthority { get; init; }

    public ScheduledServiceInvocationNyxIdCredentialSource? NyxId =>
        Source as ScheduledServiceInvocationNyxIdCredentialSource;

    public ScheduledServiceInvocationDurableCredentialReference? Durable =>
        Source as ScheduledServiceInvocationDurableCredentialReference;

    public ScheduledServiceInvocationDurableCredentialReference? DurableCredentialReference =>
        Durable;

    public ScheduledInvocationAgentKeyCredentialReference? ScheduledInvocationAgentKey =>
        Source as ScheduledInvocationAgentKeyCredentialReference;

    public ScheduledServiceInvocationNyxIdCredentialSource? SenderNyxId =>
        NyxId?.Role == ScheduledServiceInvocationNyxIdCredentialRole.Sender ? NyxId : null;

    public ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource? ScopeOwnerNyxId =>
        NyxId?.Role == ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner
            ? new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource(NyxId.Scope, NyxId.Subject)
            : null;

    private static ScheduledServiceInvocationNyxIdCredentialSource ToNyxIdSource(
        ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ScheduledServiceInvocationNyxIdCredentialSource(
            source.OwnerSubject!,
            source.Scope,
            ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner);
    }
}

public sealed record ScheduledDispatchMutationContext(
    string? AuthenticatedScopeId = null,
    ScheduledServiceInvocationNyxIdSubjectRef? AuthenticatedNyxIdOwnerSubject = null,
    TeamMemberAutomationOwner? TeamAutomationOwner = null,
    ScheduledDispatchExpectedServiceTarget? ExpectedServiceTarget = null)
{
    public static ScheduledDispatchMutationContext None { get; } = new();
}

public sealed record ScheduledDispatchExpectedServiceTarget(
    ScheduledDispatchScheduleKind ScheduleKind,
    ScheduledDispatchTargetKind TargetKind,
    ServiceIdentity ServiceIdentity,
    string ServiceEndpointId);

public sealed record ScheduledDispatchCredentialAdmissionRequest(
    ScheduledDispatchMutationContext Context,
    ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource ScopeOwnerNyxId,
    ServiceIdentity ServiceIdentity);

public enum ScheduledDispatchCredentialAdmissionStatus
{
    Allowed = 0,
    MissingBinding = 1,
    ScopeMismatch = 2,
    Unsupported = 3,
}

public sealed record ScheduledDispatchCredentialAdmissionResult(
    ScheduledDispatchCredentialAdmissionStatus Status,
    string? Error = null)
{
    public static ScheduledDispatchCredentialAdmissionResult Allowed() =>
        new(ScheduledDispatchCredentialAdmissionStatus.Allowed);

    public static ScheduledDispatchCredentialAdmissionResult MissingBinding(string? error = null) =>
        new(ScheduledDispatchCredentialAdmissionStatus.MissingBinding, error);

    public static ScheduledDispatchCredentialAdmissionResult ScopeMismatch(string? error = null) =>
        new(ScheduledDispatchCredentialAdmissionStatus.ScopeMismatch, error);

    public static ScheduledDispatchCredentialAdmissionResult Unsupported(string? error = null) =>
        new(ScheduledDispatchCredentialAdmissionStatus.Unsupported, error);
}

public interface IScheduledDispatchCredentialAdmissionPort
{
    Task<ScheduledDispatchCredentialAdmissionResult> AdmitAsync(
        ScheduledDispatchCredentialAdmissionRequest request,
        CancellationToken ct = default);
}

public sealed record ScheduledServiceInvocationCredentialExchangeResult(
    bool Succeeded,
    string? AccessToken = null,
    string? Error = null,
    DateTimeOffset? ExpiresAt = null,
    ScheduledServiceInvocationAuthorizationFailureCode? AuthorizationFailureCode = null)
{
    public static ScheduledServiceInvocationCredentialExchangeResult Success(
        string accessToken,
        DateTimeOffset? expiresAt = null) =>
        new(true, accessToken, null, expiresAt);

    public static ScheduledServiceInvocationCredentialExchangeResult Failure(
        string error,
        ScheduledServiceInvocationAuthorizationFailureCode? authorizationFailureCode = null) =>
        new(false, null, error, null, authorizationFailureCode);
}

public sealed record ScheduledDispatchConfiguration(
    string ScheduleId,
    string DisplayName,
    ScheduledDispatchTargetDescriptor Target,
    string CronExpression,
    string Timezone,
    bool Enabled,
    IReadOnlyDictionary<string, string> Headers,
    ScheduledDispatchScheduleKind ScheduleKind = ScheduledDispatchScheduleKind.Generic,
    ScheduledDispatchScheduleMode ScheduleMode = ScheduledDispatchScheduleMode.RecurringCron,
    DateTimeOffset? OneShotFireAt = null)
{
    public ScheduledDispatchCredentialRequirementTargetKind CredentialRequirementTargetKind { get; init; } =
        ScheduledDispatchCredentialRequirementTargetKind.Unspecified;

    public TeamMemberAutomationOwner? TeamAutomationOwner { get; init; }
}

public sealed record PreparedScheduledDispatchTarget(
    string? TargetActorId,
    EventEnvelope TriggerEnvelope,
    string PayloadTypeUrl,
    ScheduledDispatchTargetDescriptor Descriptor);

public sealed record ScheduledDispatchSummary(
    string ScheduleId,
    string DisplayName,
    ScheduledDispatchTargetKind TargetKind,
    string TargetActorId,
    string PayloadTypeUrl,
    string ServiceKey,
    string ServiceId,
    string ServiceEndpointId,
    string CronExpression,
    string Timezone,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? NextFireAt,
    DateTimeOffset? LastFireAt,
    string LastTargetActorId,
    string LastCommandId,
    string LastCorrelationId,
    string LastError,
    string LastErrorCode,
    int FireCount,
    int FailureCount,
    IReadOnlyDictionary<string, string> Headers,
    string ScheduleActorId,
    string? Prompt = null,
    ScheduledDispatchScheduleKind ScheduleKind = ScheduledDispatchScheduleKind.Generic,
    bool Deleted = false,
    int OverdueFireDetectedCount = 0,
    DateTimeOffset? LastOverdueFireAt = null,
    ScheduledDispatchCredentialRequirementTargetKind CredentialRequirementTargetKind =
        ScheduledDispatchCredentialRequirementTargetKind.Unspecified,
    ScheduledDispatchCredentialSourceKind CredentialSourceKind =
        ScheduledDispatchCredentialSourceKind.None,
    ScheduledDispatchScheduleMode ScheduleMode = ScheduledDispatchScheduleMode.RecurringCron,
    DateTimeOffset? OneShotFireAt = null,
    bool Completed = false,
    bool TeamOwned = false,
    string TeamOwnerScopeId = "",
    string TeamOwnerMemberId = "",
    string TeamId = "",
    TeamAutomationLifecycleStatus TeamAutomationLifecycleStatus = TeamAutomationLifecycleStatus.Unspecified,
    DateTimeOffset? CredentialExpiresAt = null,
    string TeamAutomationOperationId = "",
    long CredentialGeneration = 0,
    bool RevocationPending = false,
    string LastAuthorizationErrorCode = "",
    long StateVersion = 0,
    string PermissionDigest = "",
    string PolicyVersion = "",
    string TeamAutomationIdempotencyKey = "",
    string CredentialOwnerAuthority = "",
    string CredentialOwnerKind = "",
    string CredentialOwnerSubject = "")
{
    [JsonIgnore]
    public ServiceIdentity ServiceIdentity { get; init; } = new();

    public string OwnerLLMRouteKind { get; init; } = "unspecified";

    public string OwnerLLMRoute { get; init; } = string.Empty;

    public string OwnerLLMUserServiceId { get; init; } = string.Empty;

    public string OwnerLLMServiceSlug { get; init; } = string.Empty;

    public string OwnerLLMModel { get; init; } = string.Empty;

    public string ServiceRevisionId { get; init; } = string.Empty;

    public string NyxIdRevocationStatus { get; init; } = string.Empty;

    public string VaultRevocationStatus { get; init; } = string.Empty;
}

public sealed record ScheduledDispatchFireRecord(
    DateTimeOffset ScheduledFireAt,
    DateTimeOffset CompletedAt,
    string IdempotencyKey,
    string TargetActorId,
    string CommandId,
    string CorrelationId,
    string Error,
    string ErrorCode,
    bool Manual);

public sealed record ScheduledDispatchDetail(
    ScheduledDispatchSummary Schedule,
    IReadOnlyList<ScheduledDispatchFireRecord> RecentFires);

public sealed record ScheduledDispatchPreview(
    string CronExpression,
    string Timezone,
    IReadOnlyList<DateTimeOffset> NextFireTimes);

public sealed record ScheduledDispatchMutationReceipt(
    string ScheduleId,
    string ScheduleActorId,
    bool Accepted,
    string CommandId,
    string CorrelationId,
    DateTimeOffset AckedAt,
    string AckStage);

public sealed record TeamAutomationCommittedMutationReceipt(
    ScheduledDispatchMutationReceipt Admission,
    TeamAutomationOperationCommittedOutcome Outcome);

public sealed record ScheduledDispatchRunNowReceipt(
    string ScheduleId,
    string ScheduleActorId,
    DateTimeOffset ScheduledFireAt,
    string IdempotencyKey,
    bool Accepted,
    string CommandId,
    string CorrelationId,
    DateTimeOffset AckedAt,
    string AckStage);

public sealed record ScheduledDispatchListResult(
    IReadOnlyList<ScheduledDispatchSummary> Items,
    string? NextCursor,
    long? TotalCount);

public sealed record ScheduledDispatchListQuery(
    int Take = 50,
    string? Cursor = null,
    bool IncludeTotalCount = false,
    ScheduledDispatchTargetKind? TargetKind = null,
    string? ServiceEndpointId = null,
    ScheduledDispatchScheduleKind? ScheduleKind = null,
    TeamMemberAutomationOwner? TeamAutomationOwner = null,
    string? TeamAutomationScopeId = null,
    string? TeamAutomationTeamId = null,
    string? TeamAutomationMemberId = null,
    bool ExcludeTeamOwned = false,
    bool IncludeDeleted = false,
    bool ExcludeCompletedTeamAutomationDeletions = false,
    string? ServiceKey = null,
    string? ServiceId = null,
    string? ServiceRevisionId = null);

public interface IScheduledDispatchActorPort
{
    Task<string> EnsureScheduleActorAsync(string scheduleId, CancellationToken ct = default);

    Task<string?> ResolveScheduleActorAsync(string scheduleId, CancellationToken ct = default);

    Task<DispatchAdmission> DispatchCreateAsync(
        string actorId,
        ScheduledDispatchConfiguration configuration,
        PreparedScheduledDispatchTarget dispatch,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchUpdateAsync(
        string actorId,
        ScheduledDispatchConfiguration configuration,
        PreparedScheduledDispatchTarget dispatch,
        ScheduledDispatchExpectedServiceTarget? expectedTarget = null,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchEnsureAsync(
        string actorId,
        ScheduledDispatchConfiguration configuration,
        PreparedScheduledDispatchTarget dispatch,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchEnableAsync(
        string actorId,
        string reason,
        ScheduledDispatchExpectedServiceTarget? expectedTarget = null,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchDisableAsync(
        string actorId,
        string reason,
        ScheduledDispatchExpectedServiceTarget? expectedTarget = null,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchDeleteAsync(
        string actorId,
        string reason,
        ScheduledDispatchExpectedServiceTarget? expectedTarget = null,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchRunNowAsync(
        string actorId,
        DateTimeOffset scheduledFireAt,
        ScheduledDispatchExpectedServiceTarget? expectedTarget = null,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchBeginTeamAutomationCredentialOperationAsync(
        string actorId,
        TeamAutomationCredentialOperation operation,
        string observationRequestId,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<DispatchAdmission> DispatchRetryTeamAutomationCredentialOperationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string observationRequestId,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<DispatchAdmission> DispatchRecordTeamAutomationCredentialCandidateAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        ScheduledInvocationAgentKeyCredentialReference credential,
        ScheduledInvocationAuthorizationOwner credentialOwner,
        string observationRequestId,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<DispatchAdmission> DispatchCompleteTeamAutomationCredentialOperationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        ScheduledInvocationAgentKeyCredentialReference credential,
        ScheduledDispatchConfiguration configuration,
        PreparedScheduledDispatchTarget dispatch,
        string observationRequestId,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<DispatchAdmission> DispatchFailTeamAutomationCredentialOperationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        string errorCode,
        string observationRequestId,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<DispatchAdmission> DispatchEnableTeamAutomationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string reason,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<DispatchAdmission> DispatchDisableTeamAutomationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string reason,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<DispatchAdmission> DispatchDeleteTeamAutomationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string reason,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<DispatchAdmission> DispatchDeleteTeamAutomationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string reason,
        ScheduledInvocationAuthorizationOwner authenticatedCredentialOwner,
        string observationRequestId,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<DispatchAdmission> DispatchRetryTeamAutomationRevocationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        ScheduledInvocationAuthorizationOwner authenticatedCredentialOwner,
        string observationRequestId,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<DispatchAdmission> DispatchCompleteTeamAutomationRevocationAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        bool nyxIdRevoked,
        bool vaultRevoked,
        string errorCode,
        string observationRequestId,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<DispatchAdmission> DispatchRunTeamAutomationNowAsync(
        string actorId,
        TeamMemberAutomationOwner owner,
        DateTimeOffset scheduledFireAt,
        string operationId,
        string idempotencyKey,
        CancellationToken ct = default) =>
        throw new NotSupportedException();
}

public interface IScheduledDispatchTargetPreparationService
{
    Task<PreparedScheduledDispatchTarget> PrepareAsync(
        ScheduledDispatchConfiguration configuration,
        string commandId,
        string correlationId,
        CancellationToken ct = default);
}

public interface IScheduledDispatchQueryPort
{
    Task<ScheduledDispatchDetail?> GetAsync(string scheduleId, CancellationToken ct = default);

    Task<ScheduledDispatchListResult> ListAsync(
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default);

    Task<ScheduledDispatchListResult> ListAsync(
        ScheduledDispatchListQuery query,
        CancellationToken ct = default);
}

public sealed record ScheduledServiceInvocationDispatchReceipt(
    bool Accepted,
    string CommandId,
    string TargetActorId,
    string CorrelationId);

public sealed record ScheduledDispatchFireContext(
    DateTimeOffset FireAtUtc,
    string Timezone);

public sealed record ScheduledServiceInvocationDispatchRequest(
    ServiceInvocationRequest Request,
    ScheduledServiceInvocationAuth? Auth = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    bool ProjectNyxIdAccessTokenToWorkflowCallerCredential = false,
    string? ScheduleId = null,
    ScheduledInvocationAuthorizationFact? AuthorizationFact = null,
    ScheduledDispatchFireContext? FireContext = null,
    string? ScheduleOperationId = null);

public interface IScheduledServiceInvocationDispatchPort
{
    Task<ScheduledServiceInvocationDispatchReceipt> DispatchAsync(
        ScheduledServiceInvocationDispatchRequest dispatch,
        CancellationToken ct = default);
}

public sealed class ScheduledWorkflowAdmissionException : InvalidOperationException
{
    public ScheduledWorkflowAdmissionException(string stableCode, string safeMessage)
        : base(string.IsNullOrWhiteSpace(safeMessage)
            ? "Workflow admission was rejected."
            : safeMessage.Trim())
    {
        StableCode = stableCode?.Trim() switch
        {
            "WORKFLOW_DEFINITION_INVALID" => "WORKFLOW_DEFINITION_INVALID",
            "NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED" =>
                "NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED",
            "CAPABILITY_ADMISSION_REBIND_REQUIRED" => "CAPABILITY_ADMISSION_REBIND_REQUIRED",
            _ => "WORKFLOW_ADMISSION_REJECTED",
        };
        SafeMessage = Message;
    }

    public string StableCode { get; }
    public string SafeMessage { get; }
}

public interface IScheduledServiceInvocationCredentialExchangePort
{
    Task<ScheduledServiceInvocationCredentialExchangeResult> IssueNyxIdAsync(
        ScheduledServiceInvocationNyxIdCredentialSource source,
        CancellationToken ct = default);
}

public interface IScheduledDispatchApplicationService
{
    Task<ScheduledDispatchMutationReceipt> CreateAsync(
        ScheduledDispatchConfiguration configuration,
        ScheduledDispatchMutationContext? context = null,
        CancellationToken ct = default);

    Task<ScheduledDispatchMutationReceipt> EnsureAsync(
        ScheduledDispatchConfiguration configuration,
        ScheduledDispatchMutationContext? context = null,
        CancellationToken ct = default);

    Task<ScheduledDispatchMutationReceipt> UpdateAsync(
        string scheduleId,
        ScheduledDispatchConfiguration configuration,
        ScheduledDispatchMutationContext? context = null,
        CancellationToken ct = default);

    Task<ScheduledDispatchMutationReceipt> EnableAsync(
        string scheduleId,
        string reason,
        ScheduledDispatchMutationContext? context = null,
        CancellationToken ct = default);

    Task<ScheduledDispatchMutationReceipt> DisableAsync(
        string scheduleId,
        string reason,
        ScheduledDispatchMutationContext? context = null,
        CancellationToken ct = default);

    Task<ScheduledDispatchMutationReceipt> DeleteAsync(
        string scheduleId,
        string reason,
        ScheduledDispatchMutationContext? context = null,
        CancellationToken ct = default);

    Task<ScheduledDispatchMutationReceipt> DeleteTeamAutomationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string reason,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<ScheduledDispatchDetail?> GetAsync(
        string scheduleId,
        CancellationToken ct = default);

    Task<ScheduledDispatchListResult> ListAsync(
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default);

    Task<ScheduledDispatchListResult> ListAsync(
        ScheduledDispatchListQuery query,
        CancellationToken ct = default);

    Task<ScheduledDispatchPreview> PreviewAsync(
        string cronExpression,
        string? timezone,
        int count,
        DateTimeOffset? fromUtc = null,
        CancellationToken ct = default);

    Task<ScheduledDispatchRunNowReceipt> RunNowAsync(
        string scheduleId,
        ScheduledDispatchMutationContext? context = null,
        CancellationToken ct = default);

    Task<TeamAutomationCommittedMutationReceipt> BeginTeamAutomationCredentialOperationAsync(
        TeamAutomationCredentialOperation operation,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<TeamAutomationCommittedMutationReceipt> RetryTeamAutomationCredentialOperationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<TeamAutomationCommittedMutationReceipt> RecordTeamAutomationCredentialCandidateAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        ScheduledInvocationAgentKeyCredentialReference credential,
        ScheduledInvocationAuthorizationOwner credentialOwner,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<TeamAutomationCommittedMutationReceipt> CompleteTeamAutomationCredentialOperationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        ScheduledInvocationAgentKeyCredentialReference credential,
        ScheduledDispatchConfiguration configuration,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<TeamAutomationCommittedMutationReceipt> FailTeamAutomationCredentialOperationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        string errorCode,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<ScheduledDispatchMutationReceipt> EnableTeamAutomationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string reason,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<ScheduledDispatchMutationReceipt> DisableTeamAutomationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string reason,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<TeamAutomationCommittedMutationReceipt> DeleteTeamAutomationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string reason,
        ScheduledInvocationAuthorizationOwner authenticatedCredentialOwner,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<TeamAutomationCommittedMutationReceipt> RetryTeamAutomationRevocationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        ScheduledInvocationAuthorizationOwner authenticatedCredentialOwner,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<TeamAutomationCommittedMutationReceipt> CompleteTeamAutomationRevocationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        string effectAttemptId,
        bool nyxIdRevoked,
        bool vaultRevoked,
        string errorCode,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<ScheduledDispatchRunNowReceipt> RunTeamAutomationNowAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<ScheduledDispatchRunNowReceipt> RunTeamAutomationNowAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        string operationId,
        string idempotencyKey,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<ScheduledDispatchDetail?> GetTeamScheduleAsync(
        string scheduleId,
        string scopeId,
        string? teamId = null,
        string? memberId = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<ScheduledDispatchDetail?> GetTeamAutomationAsync(
        string scheduleId,
        TeamMemberAutomationOwner owner,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<ScheduledDispatchListResult> ListTeamAutomationsAsync(
        TeamMemberAutomationOwner owner,
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default) =>
        throw new NotSupportedException();
}

public abstract class ScheduledDispatchApplicationException : Exception
{
    protected ScheduledDispatchApplicationException(string scheduleId, string message)
        : base(message)
    {
        ScheduleId = scheduleId;
    }

    public string ScheduleId { get; }
}

public sealed class ScheduledDispatchNotFoundException : ScheduledDispatchApplicationException
{
    public ScheduledDispatchNotFoundException(string scheduleId)
        : base(scheduleId, $"Scheduled dispatch '{scheduleId}' was not found.")
    {
    }
}

public sealed class ScheduledDispatchConflictException : ScheduledDispatchApplicationException
{
    public ScheduledDispatchConflictException(string scheduleId, string message)
        : base(scheduleId, message)
    {
    }
}
