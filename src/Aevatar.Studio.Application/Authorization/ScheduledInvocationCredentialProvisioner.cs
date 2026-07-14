namespace Aevatar.Studio.Application.Authorization;

public sealed class ScheduledInvocationCredentialProvisioner : IScheduledInvocationCredentialProvisioner
{
    private readonly IScheduledInvocationAuthorizationPlanner _planner;
    private readonly IScheduledInvocationCredentialIssuer _issuer;

    public ScheduledInvocationCredentialProvisioner(
        IScheduledInvocationAuthorizationPlanner planner,
        IScheduledInvocationCredentialIssuer issuer)
    {
        _planner = planner;
        _issuer = issuer;
    }

    public async Task<ScheduledInvocationCredentialIssueResult> ProvisionAsync(
        string ownerBearer,
        ScheduledInvocationAuthorizationRequest request,
        string confirmedPermissionDigest,
        string credentialName,
        CancellationToken ct = default)
    {
        var current = await _planner.PlanAsync(request, ct);
        if (!current.Success)
            return new(false, current.Detail, string.Empty, string.Empty, 0);
        if (!string.Equals(current.Plan!.PermissionDigest, confirmedPermissionDigest, StringComparison.Ordinal))
            return new(false, "authorization_plan_changed", string.Empty, string.Empty, 0);

        return await _issuer.IssueAsync(ownerBearer, current.Plan, credentialName, ct);
    }
}
