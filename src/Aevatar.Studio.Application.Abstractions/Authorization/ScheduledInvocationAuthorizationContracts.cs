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

public interface IScheduledInvocationAuthorizationPlanner
{
    Task<ScheduledInvocationAuthorizationPlanResult> PlanAsync(
        ScheduledInvocationAuthorizationRequest request,
        CancellationToken ct = default);
}
