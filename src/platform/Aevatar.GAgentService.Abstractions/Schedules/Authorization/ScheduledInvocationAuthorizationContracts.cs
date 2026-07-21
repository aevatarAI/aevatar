using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;

namespace Aevatar.GAgentService.Abstractions.Schedules.Authorization;

public static class ScheduledInvocationAuthorizationContractVersions
{
    public const string Schema = "scheduled-invocation-authorization/v2";
    public const string CredentialPolicy = "nyxid-api-key/scheduled-invocation/v2";
}

public sealed record AuthenticatedAuthorizationOwnerContext(
    AuthorizationOwnerIdentity Owner,
    string SubjectPlatform,
    string SubjectTenant,
    string SubjectExternalUserId,
    string VerifiedBindingId,
    AuthorizationOwnerIdentity? AuthenticatedActor = null);

public sealed record ScheduledInvocationAuthorizationRequest(
    ScheduledInvocationTarget InvocationTarget,
    AuthenticatedAuthorizationOwnerContext OwnerContext,
    IReadOnlyList<string> RequiredNyxIdServiceIds,
    IReadOnlyList<string> RequiredNyxIdServiceSlugs,
    AuthorizationGrantRequirement ServiceGrantRequirement,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset EvaluatedAtUtc,
    IReadOnlyList<AuthorizationSourceStamp>? SourceStamps = null)
{
    public AuthorizationOwnerIdentity Owner => OwnerContext.Owner;
}

public sealed record ScheduledInvocationAuthorizationPlanResult(
    ScheduledInvocationAuthorizationPlan? Plan,
    ScheduledInvocationAuthorizationFailureCode FailureCode,
    string Detail,
    long ObservedCatalogStateVersion = 0)
{
    public bool Success => Plan is not null;

    public static ScheduledInvocationAuthorizationPlanResult Succeeded(
        ScheduledInvocationAuthorizationPlan plan) =>
        new(
            plan,
            ScheduledInvocationAuthorizationFailureCode.Unspecified,
            string.Empty,
            plan.CatalogAuthority?.ActorStateVersion ?? 0);

    public static ScheduledInvocationAuthorizationPlanResult Failed(
        ScheduledInvocationAuthorizationFailureCode failureCode,
        string detail,
        long observedCatalogStateVersion = 0) =>
        new(null, failureCode, detail, observedCatalogStateVersion);
}

public sealed class ValidatedScheduledInvocationAuthorizationPlan
{
    private readonly ScheduledInvocationAuthorizationPlan _plan;

    internal ValidatedScheduledInvocationAuthorizationPlan(
        ScheduledInvocationAuthorizationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _plan = plan.Clone();
    }

    public ScheduledInvocationAuthorizationPlan Plan => _plan.Clone();

    public bool HasValidIntegrity => ScheduledInvocationAuthorizationPlanIntegrity.IsValid(_plan);
}

public static class ScheduledInvocationAuthorizationPlanIntegrity
{
    public static string ComputeDigest(ScheduledInvocationAuthorizationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var canonical = plan.Clone();
        canonical.PermissionDigest = string.Empty;
        return Convert.ToHexStringLower(SHA256.HashData(canonical.ToByteArray()));
    }

    public static bool IsValid(ScheduledInvocationAuthorizationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return !string.IsNullOrWhiteSpace(plan.PermissionDigest) &&
               string.Equals(
                   plan.PermissionDigest,
                   ComputeDigest(plan),
                   StringComparison.Ordinal);
    }
}

public static class NyxIdAuthorizationCatalogIntegrity
{
    public static string ComputeContentDigest(
        AuthorizationOwnerIdentity owner,
        IEnumerable<NyxIdAuthorizationServiceEvidence> services)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(services);
        var content = new NyxIdAuthorizationCatalogContent { Owner = owner.Clone() };
        content.Services.Add(services.Select(static service => service.Clone()));
        return Convert.ToHexStringLower(SHA256.HashData(content.ToByteArray()));
    }
}

public sealed record ScheduledInvocationAuthorizationValidationResult(
    ValidatedScheduledInvocationAuthorizationPlan? ValidatedPlan,
    ScheduledInvocationAuthorizationFailureCode FailureCode,
    string Detail,
    long RequiredStateVersion = 0,
    long ObservedCatalogStateVersion = 0)
{
    public bool Success => ValidatedPlan is not null;

    internal static ScheduledInvocationAuthorizationValidationResult Succeeded(
        ScheduledInvocationAuthorizationPlan plan) =>
        new(new ValidatedScheduledInvocationAuthorizationPlan(plan),
            ScheduledInvocationAuthorizationFailureCode.Unspecified,
            string.Empty,
            ObservedCatalogStateVersion: plan.CatalogAuthority?.ActorStateVersion ?? 0);

    public static ScheduledInvocationAuthorizationValidationResult Failed(
        ScheduledInvocationAuthorizationFailureCode failureCode,
        string detail,
        long observedCatalogStateVersion = 0) =>
        new(
            null,
            failureCode,
            detail,
            ObservedCatalogStateVersion: observedCatalogStateVersion);

    public static ScheduledInvocationAuthorizationValidationResult ProjectionPending(
        long requiredStateVersion,
        long observedCatalogStateVersion)
    {
        if (requiredStateVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredStateVersion));
        if (observedCatalogStateVersion < 0 || observedCatalogStateVersion >= requiredStateVersion)
            throw new ArgumentOutOfRangeException(nameof(observedCatalogStateVersion));

        return new(
            null,
            ScheduledInvocationAuthorizationFailureCode.CatalogProjectionPending,
            "nyxid_catalog_projection_pending",
            requiredStateVersion,
            observedCatalogStateVersion);
    }
}

public sealed record NyxIdAuthorizationCatalogSnapshot(
    AuthorizationOwnerIdentity Owner,
    long StateVersion,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset FreshUntilUtc,
    string ContractVersion,
    string PolicyVersion,
    DateTimeOffset EvaluatedAtUtc,
    string ContentDigest,
    IReadOnlyList<NyxIdAuthorizationServiceEvidence> Services,
    bool Invalidated = false,
    string InvalidationReason = "",
    DateTimeOffset? LastRefreshFailedAtUtc = null,
    string LastRefreshFailureCode = "",
    long LifecycleFence = 0,
    bool Activated = false,
    bool Cleaned = false,
    DateTimeOffset? CleanedAtUtc = null,
    string CleanupReason = "");

public enum NyxIdAuthorizationCatalogVisibilityStatus
{
    Unspecified = 0,
    Ready = 1,
    ProjectionPending = 2,
    OwnerMismatch = 3,
    Invalidated = 4,
    Stale = 5,
    Invalid = 6,
    Unavailable = 7,
}

public sealed record NyxIdAuthorizationCatalogVisibilityResult(
    NyxIdAuthorizationCatalogVisibilityStatus Status,
    long RequiredStateVersion,
    long VisibleStateVersion,
    string FailureCode)
{
    public bool Ready => Status == NyxIdAuthorizationCatalogVisibilityStatus.Ready;
    public bool ProjectionPending => Status == NyxIdAuthorizationCatalogVisibilityStatus.ProjectionPending;

    public static NyxIdAuthorizationCatalogVisibilityResult Unavailable(long requiredStateVersion) =>
        new(
            NyxIdAuthorizationCatalogVisibilityStatus.Unavailable,
            requiredStateVersion,
            0,
            "nyxid_catalog_visibility_unavailable");
}

public sealed record NyxIdAuthorizationCatalogObservation(
    AuthorizationOwnerIdentity Owner,
    string RefreshId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset FreshUntilUtc,
    string ContractVersion,
    string PolicyVersion,
    DateTimeOffset EvaluatedAtUtc,
    string ContentDigest,
    IReadOnlyList<NyxIdAuthorizationServiceEvidence> Services);

public enum NyxIdAuthorizationCatalogRefreshStatus
{
    Unspecified = 0,
    Observed = 1,
    AccessDenied = 2,
    Failed = 3,
    ObservationTimedOut = 4,
    OwnerNotSupported = 5,
    CatalogUnstable = 6,
    Superseded = 7,
}

public sealed record NyxIdAuthorizationCatalogRefreshResult(
    NyxIdAuthorizationCatalogRefreshStatus Status,
    string FailureCode,
    long StateVersion = 0)
{
    public bool Success => Status == NyxIdAuthorizationCatalogRefreshStatus.Observed;

    public static NyxIdAuthorizationCatalogRefreshResult ObservedAt(long stateVersion)
    {
        if (stateVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(stateVersion));

        return new(NyxIdAuthorizationCatalogRefreshStatus.Observed, string.Empty, stateVersion);
    }
}

public sealed record ScheduledInvocationMemberEvidence(
    long StateVersion,
    string DraftWorkflowId,
    string WorkflowRevisionId,
    string PublishedServiceId);

public sealed record ScheduledInvocationWorkflowEvidence(
    long StateVersion,
    IReadOnlyList<string> ConnectorCapabilityRefs,
    bool OwnerLLMRouteRequired,
    IReadOnlyList<string> NyxIdServiceIds,
    IReadOnlyList<string> NyxIdServiceSlugs,
    AuthorizationGrantRequirement ServiceGrantRequirement);

public sealed record ScheduledInvocationConnectorEvidence(
    long StateVersion,
    IReadOnlyList<string> ConnectorCapabilityRefs);

public sealed record ScheduledInvocationOwnerLLMEvidence(
    long StateVersion,
    string NyxIdServiceId,
    string NyxIdServiceSlug,
    AuthorizationGrantRequirement ServiceGrantRequirement);

public sealed class ScheduledInvocationOwnerLLMRouteOptions
{
    public string DefaultRoutePreference { get; set; } = string.Empty;
}

public interface INyxIdAuthorizationCatalogQueryPort
{
    Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
        AuthorizationOwnerIdentity owner,
        CancellationToken ct = default);
}

public interface INyxIdAuthorizationCatalogCommandPort
{
    Task BeginRefreshAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        DateTimeOffset startedAtUtc,
        long expectedLifecycleFence,
        CancellationToken ct = default);

    Task ObserveAsync(NyxIdAuthorizationCatalogObservation observation, CancellationToken ct = default);

    Task RecordRefreshFailureAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        DateTimeOffset failedAtUtc,
        string failureCode,
        CancellationToken ct = default);

    Task InvalidateAsync(
        AuthorizationOwnerIdentity owner,
        DateTimeOffset invalidatedAtUtc,
        string reason,
        CancellationToken ct = default);

    Task InvalidateRefreshAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        DateTimeOffset invalidatedAtUtc,
        string reason,
        NyxIdAuthorizationCatalogRefreshOutcomeStatus outcomeStatus,
        CancellationToken ct = default);

    Task CleanupAsync(
        AuthorizationOwnerIdentity owner,
        DateTimeOffset cleanedAtUtc,
        string reason,
        CancellationToken ct = default);
}

public interface INyxIdAuthorizationCatalogRefreshPort
{
    Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
        AuthorizationOwnerIdentity owner,
        string bearerToken,
        CancellationToken ct = default);

    Task<NyxIdAuthorizationCatalogRefreshResult> RefreshPersonalAsync(
        string verifiedOwnerSubject,
        string bearerToken,
        CancellationToken ct = default);
}

public interface INyxIdAuthorizationCatalogVisibilityPort
{
    Task<NyxIdAuthorizationCatalogVisibilityResult> ResolveAsync(
        AuthorizationOwnerIdentity owner,
        long requiredStateVersion,
        CancellationToken ct = default);
}

public interface IScheduledInvocationMemberEvidenceQueryPort
{
    Task<ScheduledInvocationMemberEvidence?> GetAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default);
}

public interface IScheduledInvocationWorkflowEvidenceQueryPort
{
    Task<ScheduledInvocationWorkflowEvidence?> GetAsync(
        string scopeId,
        string publishedServiceId,
        string workflowRevisionId,
        CancellationToken ct = default);
}

public interface IScheduledInvocationConnectorEvidenceQueryPort
{
    Task<ScheduledInvocationConnectorEvidence?> GetAsync(
        string scopeId,
        CancellationToken ct = default);
}

public interface IScheduledInvocationOwnerLLMEvidenceQueryPort
{
    Task<ScheduledInvocationOwnerLLMEvidence?> GetAsync(
        string scopeId,
        CancellationToken ct = default);
}

public interface IScheduledInvocationAuthorizationPlanner
{
    Task<ScheduledInvocationAuthorizationPlanResult> PlanAsync(
        ScheduledInvocationAuthorizationRequest request,
        CancellationToken ct = default);
}

public interface IScheduledInvocationAuthorizationRevalidator
{
    Task<ScheduledInvocationAuthorizationValidationResult> RevalidateAsync(
        ScheduledInvocationAuthorizationRequest request,
        ScheduledInvocationAuthorizationConfirmation confirmation,
        CancellationToken ct = default);
}

public static class ScheduledInvocationAuthorizationConfirmations
{
    public static ScheduledInvocationAuthorizationConfirmation FromPlan(
        ScheduledInvocationAuthorizationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new ScheduledInvocationAuthorizationConfirmation
        {
            InvocationTarget = plan.InvocationTarget?.Clone(),
            Owner = plan.Owner?.Clone(),
            SchemaVersion = plan.SchemaVersion,
            PolicyVersion = plan.CredentialPolicy?.PolicyVersion ?? string.Empty,
            PermissionDigest = plan.PermissionDigest,
        };
    }
}

public static class NyxIdAuthorizationCatalogActorIds
{
    private const string Prefix = "gagent-service-nyxid-authorization-catalog";

    public static string Build(AuthorizationOwnerIdentity owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var authority = owner.Authority?.Trim() ?? string.Empty;
        var subject = owner.OwnerSubject?.Trim() ?? string.Empty;
        if (authority.Length == 0 || subject.Length == 0 || owner.OwnerKind == AuthorizationOwnerKind.Unspecified)
            throw new ArgumentException("Authorization owner identity is incomplete.", nameof(owner));

        var identity = $"{authority}\n{(int)owner.OwnerKind}\n{subject}";
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return $"{Prefix}:{digest}";
    }
}

public static class NyxIdAuthorizationAuthorities
{
    public const string NyxId = "nyxid";
}
