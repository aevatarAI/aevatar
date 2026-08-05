using Aevatar.Workflow.Abstractions;

namespace Aevatar.GAgentService.Abstractions.Schedules.Authorization;

public enum NyxIdScheduledOperationAuthorizationDecision
{
    Unspecified = 0,
    AutoAllow = 1,
    ReusableGrantRequired = 2,
    PerRequestApprovalRequired = 3,
    Denied = 4,
    AuthorityContractUnavailable = 5,
}

public sealed record NyxIdScheduledOperationAuthorizationRequest(
    StudioMemberInvocationTarget InvocationTarget,
    AuthorizationOwnerIdentity Owner,
    AuthorizationOwnerIdentity AuthenticatedActor,
    string SubjectPlatform,
    string SubjectTenant,
    string SubjectExternalUserId,
    string VerifiedBindingId,
    NyxIdRequestSelector Request,
    NyxIdExplicitRequestGrant DurableRequestGrant,
    DateTimeOffset EvaluatedAtUtc);

public sealed record NyxIdScheduledOperationAuthorizationResult(
    NyxIdScheduledOperationAuthorizationDecision Decision);

public interface INyxIdScheduledOperationAuthorizationPort
{
    Task<NyxIdScheduledOperationAuthorizationResult> EvaluateAsync(
        NyxIdScheduledOperationAuthorizationRequest request,
        CancellationToken ct = default);
}
