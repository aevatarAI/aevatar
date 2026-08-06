using Aevatar.GAgentService.Abstractions.Schedules.Authorization;

namespace Aevatar.GAgentService.Application.Schedules.Authorization;

public sealed class ScheduledInvocationAuthorizationRevalidator
    : IScheduledInvocationAuthorizationRevalidator
{
    private readonly IScheduledInvocationAuthorizationPlanner _planner;
    private readonly TimeProvider _timeProvider;

    public ScheduledInvocationAuthorizationRevalidator(
        IScheduledInvocationAuthorizationPlanner planner,
        TimeProvider timeProvider)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<ScheduledInvocationAuthorizationValidationResult> RevalidateAsync(
        ScheduledInvocationAuthorizationRequest request,
        ScheduledInvocationAuthorizationConfirmation confirmation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(confirmation);
        var currentRequest = request with { EvaluatedAtUtc = _timeProvider.GetUtcNow() };
        var current = await _planner.PlanAsync(currentRequest, ct);
        if (!current.Success)
        {
            var failureCode = current.FailureCode is
                ScheduledInvocationAuthorizationFailureCode.OwnerLlmRouteUnavailable or
                ScheduledInvocationAuthorizationFailureCode.OwnerLlmModelNotVerifiable or
                ScheduledInvocationAuthorizationFailureCode.OwnerLlmModelUnavailable
                    ? current.FailureCode
                    : ScheduledInvocationAuthorizationFailureCode.AuthorizationPlanChanged;
            return ScheduledInvocationAuthorizationValidationResult.Failed(
                failureCode,
                current.Detail,
                current.ObservedCatalogStateVersion,
                current.RequiredNyxIdServices,
                current.LLMRefreshRequirement);
        }

        var plan = current.Plan!;
        if (!ScheduledInvocationAuthorizationPlanIntegrity.IsValid(plan) ||
            confirmation.InvocationTarget == null ||
            confirmation.Owner == null ||
            !confirmation.InvocationTarget.Equals(plan.InvocationTarget) ||
            !confirmation.Owner.Equals(plan.Owner) ||
            !string.Equals(confirmation.SchemaVersion, plan.SchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(confirmation.PolicyVersion, plan.CredentialPolicy?.PolicyVersion, StringComparison.Ordinal) ||
            !string.Equals(confirmation.PermissionDigest, plan.PermissionDigest, StringComparison.Ordinal))
        {
            return ScheduledInvocationAuthorizationValidationResult.Failed(
                ScheduledInvocationAuthorizationFailureCode.AuthorizationPlanChanged,
                "scheduled_invocation_authorization_plan_changed",
                current.ObservedCatalogStateVersion);
        }

        return ScheduledInvocationAuthorizationValidationResult.Succeeded(plan);
    }
}
