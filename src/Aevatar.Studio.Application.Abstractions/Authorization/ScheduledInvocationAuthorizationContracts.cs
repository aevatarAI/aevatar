using Aevatar.Workflow.Abstractions;

namespace Aevatar.Studio.Application.Authorization;

public sealed record ScheduledInvocationAuthorizationRequest(
    ScheduledInvocationTarget InvocationTarget,
    AuthenticatedNyxIdOwnerContext OwnerContext,
    IReadOnlyList<string> RequiredNyxIdServiceIds,
    ScheduledInvocationAuthorizationAuthority Authority,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset EvaluatedAtUtc)
{
    public NyxIdCatalogOwnerIdentity Owner => OwnerContext.Owner;

    public IReadOnlyList<string> RequiredNyxIdServiceSlugs { get; init; } = [];

    public bool ServiceGrantsNotRequired { get; init; }
}

public sealed record ScheduledInvocationAuthorizationPlanResult(
    ScheduledInvocationAuthorizationPlan? Plan,
    ScheduledInvocationAuthorizationFailureCode FailureCode,
    string Detail)
{
    public bool Success => Plan is not null;

    public static ScheduledInvocationAuthorizationPlanResult Succeeded(ScheduledInvocationAuthorizationPlan plan) =>
        new(plan, ScheduledInvocationAuthorizationFailureCode.Unspecified, string.Empty);

    public static ScheduledInvocationAuthorizationPlanResult Failed(
        ScheduledInvocationAuthorizationFailureCode failureCode,
        string detail) => new(null, failureCode, detail);
}

public sealed record NyxIdCatalogSnapshot(
    NyxIdCatalogOwnerIdentity Owner,
    long StateVersion,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset FreshUntilUtc,
    string ExternalRevision,
    string ContentDigest,
    IReadOnlyList<NyxIdServiceGrant> Services,
    IReadOnlySet<string>? UnreachableServiceIds = null);

public interface INyxIdCatalogSnapshotQueryPort
{
    Task<NyxIdCatalogSnapshot?> GetAsync(NyxIdCatalogOwnerIdentity owner, CancellationToken ct = default);
}

public sealed record ScheduledInvocationMemberFact(
    long StateVersion,
    string WorkflowId,
    string WorkflowRevision,
    string PublishedServiceId);

public sealed record ScheduledInvocationWorkflowFact(
    long StateVersion,
    WorkflowAuthorizationDependencies Dependencies);

public sealed record ScheduledInvocationVersionFact(long StateVersion);

public interface IScheduledInvocationMemberQueryPort
{
    Task<ScheduledInvocationMemberFact?> GetAsync(string scopeId, string memberId, CancellationToken ct = default);
}

public interface IScheduledInvocationWorkflowQueryPort
{
    Task<ScheduledInvocationWorkflowFact?> GetAsync(string workflowId, CancellationToken ct = default);
}

public interface IScheduledInvocationConnectorQueryPort
{
    Task<ScheduledInvocationVersionFact?> GetAsync(string scopeId, CancellationToken ct = default);
}

public interface IScheduledInvocationOwnerLLMQueryPort
{
    Task<ScheduledInvocationVersionFact?> GetAsync(string scopeId, CancellationToken ct = default);
}

public sealed record NyxIdCatalogObservation(
    NyxIdCatalogOwnerIdentity Owner,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset FreshUntilUtc,
    string ExternalRevision,
    string ContentDigest,
    IReadOnlyList<NyxIdServiceGrant> Services);

public interface INyxIdCatalogSnapshotCommandPort
{
    Task ObserveAsync(NyxIdCatalogObservation observation, CancellationToken ct = default);

    Task RecordRefreshFailureAsync(
        NyxIdCatalogOwnerIdentity owner,
        DateTimeOffset failedAtUtc,
        string failureCode,
        CancellationToken ct = default);

    Task InvalidateAsync(
        NyxIdCatalogOwnerIdentity owner,
        DateTimeOffset invalidatedAtUtc,
        string reason,
        CancellationToken ct = default);
}

public interface IScheduledInvocationAuthorizationPlanner
{
    Task<ScheduledInvocationAuthorizationPlanResult> PlanAsync(
        ScheduledInvocationAuthorizationRequest request,
        CancellationToken ct = default);
}
