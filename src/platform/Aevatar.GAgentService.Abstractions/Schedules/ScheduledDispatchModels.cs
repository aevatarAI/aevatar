using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions;

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
    SkillRunner = 2,
}

public enum ScheduledDispatchScheduleMode
{
    RecurringCron = 0,
    OneShotAtUtc = 1,
}

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
    ScheduledServiceInvocationAuth? Auth = null);

public sealed record ScheduledServiceInvocationIdentitySubject(
    string Platform,
    string Tenant,
    string ExternalUserId);

public enum ScheduledServiceInvocationIdentityCredentialRole
{
    Sender = 1,
    ScopeOwner = 2,
}

public abstract record ScheduledServiceInvocationCredentialSource;

public sealed record ScheduledServiceInvocationIdentityCredentialSource(
    ScheduledServiceInvocationIdentitySubject Subject,
    string Scope,
    ScheduledServiceInvocationIdentityCredentialRole Role = ScheduledServiceInvocationIdentityCredentialRole.Sender)
    : ScheduledServiceInvocationCredentialSource;

public sealed record ScheduledServiceInvocationScopeOwnerCredentialSource(
    string Scope,
    ScheduledServiceInvocationIdentitySubject? OwnerSubject = null);

public sealed record ScheduledInvocationAgentKeyCredentialReference(
    SecretReference SecretReference,
    string ApiKeyId,
    long KeyExpiresAtUnixMs)
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

    public ScheduledServiceInvocationAuth(ScheduledServiceInvocationIdentityCredentialSource SenderIdentity)
        : this((ScheduledServiceInvocationCredentialSource)(SenderIdentity ??
                                                            throw new ArgumentNullException(nameof(SenderIdentity))))
    {
    }

    public ScheduledServiceInvocationAuth(ScheduledServiceInvocationScopeOwnerCredentialSource ScopeOwnerIdentity)
        : this((ScheduledServiceInvocationCredentialSource)ToIdentitySource(ScopeOwnerIdentity))
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

    public ScheduledServiceInvocationIdentityCredentialSource? Identity =>
        Source as ScheduledServiceInvocationIdentityCredentialSource;

    public ScheduledServiceInvocationDurableCredentialReference? Durable =>
        Source as ScheduledServiceInvocationDurableCredentialReference;

    public ScheduledServiceInvocationDurableCredentialReference? DurableCredentialReference =>
        Durable;

    public ScheduledInvocationAgentKeyCredentialReference? ScheduledInvocationAgentKey =>
        Source as ScheduledInvocationAgentKeyCredentialReference;

    public ScheduledServiceInvocationIdentityCredentialSource? SenderIdentity =>
        Identity?.Role == ScheduledServiceInvocationIdentityCredentialRole.Sender ? Identity : null;

    public ScheduledServiceInvocationScopeOwnerCredentialSource? ScopeOwnerIdentity =>
        Identity?.Role == ScheduledServiceInvocationIdentityCredentialRole.ScopeOwner
            ? new ScheduledServiceInvocationScopeOwnerCredentialSource(Identity.Scope, Identity.Subject)
            : null;

    private static ScheduledServiceInvocationIdentityCredentialSource ToIdentitySource(
        ScheduledServiceInvocationScopeOwnerCredentialSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ScheduledServiceInvocationIdentityCredentialSource(
            source.OwnerSubject!,
            source.Scope,
            ScheduledServiceInvocationIdentityCredentialRole.ScopeOwner);
    }
}

public sealed record ScheduledDispatchMutationContext(
    string? AuthenticatedScopeId = null,
    ScheduledServiceInvocationIdentitySubject? AuthenticatedIdentityOwnerSubject = null)
{
    public static ScheduledDispatchMutationContext None { get; } = new();
}

public sealed record ScheduledDispatchCredentialAdmissionRequest(
    ScheduledDispatchMutationContext Context,
    ScheduledServiceInvocationScopeOwnerCredentialSource ScopeOwnerIdentity,
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
    DateTimeOffset? ExpiresAt = null)
{
    public static ScheduledServiceInvocationCredentialExchangeResult Success(
        string accessToken,
        DateTimeOffset? expiresAt = null) =>
        new(true, accessToken, null, expiresAt);

    public static ScheduledServiceInvocationCredentialExchangeResult Failure(string error) =>
        new(false, null, error, null);
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
    bool Completed = false);

public sealed record ScheduledDispatchFireRecord(
    DateTimeOffset ScheduledFireAt,
    DateTimeOffset CompletedAt,
    string IdempotencyKey,
    string TargetActorId,
    string CommandId,
    string CorrelationId,
    string Error,
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
    ScheduledDispatchScheduleKind? ScheduleKind = null);

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
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchEnsureAsync(
        string actorId,
        ScheduledDispatchConfiguration configuration,
        PreparedScheduledDispatchTarget dispatch,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchEnableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchDisableAsync(
        string actorId,
        string reason,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchDeleteAsync(
        string actorId,
        string reason,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchRunNowAsync(
        string actorId,
        DateTimeOffset scheduledFireAt,
        CancellationToken ct = default);
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

public sealed record ScheduledServiceInvocationDispatchRequest(
    ServiceInvocationRequest Request,
    ScheduledServiceInvocationAuth? Auth = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    bool ProjectNyxIdAccessTokenToWorkflowCallerCredential = false,
    string? ScheduleId = null);

public interface IScheduledServiceInvocationDispatchPort
{
    Task<ScheduledServiceInvocationDispatchReceipt> DispatchAsync(
        ScheduledServiceInvocationDispatchRequest dispatch,
        CancellationToken ct = default);
}

public interface IScheduledServiceInvocationCredentialExchangePort
{
    Task<ScheduledServiceInvocationCredentialExchangeResult> IssueAsync(
        ScheduledServiceInvocationIdentityCredentialSource source,
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
        CancellationToken ct = default);

    Task<ScheduledDispatchMutationReceipt> DisableAsync(
        string scheduleId,
        string reason,
        CancellationToken ct = default);

    Task<ScheduledDispatchMutationReceipt> DeleteAsync(
        string scheduleId,
        string reason,
        CancellationToken ct = default);

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
        CancellationToken ct = default);
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
