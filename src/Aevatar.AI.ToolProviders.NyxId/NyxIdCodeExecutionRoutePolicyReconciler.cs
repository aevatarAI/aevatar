namespace Aevatar.AI.ToolProviders.NyxId;

public enum NyxIdCodeExecutionRouteRepairFailureKind
{
    None = 0,
    UpdateException = 1,
    PostconditionMismatch = 2,
}

public sealed record NyxIdCodeExecutionRouteReconciliation(
    NyxIdCodeExecutionRouteResolution Resolution,
    bool Attempted,
    bool Verified,
    NyxIdCodeExecutionRouteRepairFailureKind FailureKind =
        NyxIdCodeExecutionRouteRepairFailureKind.None);

/// <summary>
/// Declares the platform code route contract and selects its exact caller-owned UserService. The
/// generic route converger owns authority joining, mutation, scope preservation, and readback.
/// </summary>
public sealed class NyxIdCodeExecutionRoutePolicyReconciler
{
    private static readonly NyxIdUserServiceRouteContract RouteContract = new(
        NyxIdUserServiceBooleanRequirement.Disabled,
        NyxIdUserServiceBooleanRequirement.Enabled,
        ["sandbox:execute"]);

    private readonly NyxIdUserServiceRouteConverger _converger;

    public NyxIdCodeExecutionRoutePolicyReconciler(INyxIdApiClientFactory clientFactory)
    {
        _converger = new NyxIdUserServiceRouteConverger(clientFactory);
    }

    public async Task<NyxIdCodeExecutionRouteReconciliation> ReconcileAsync(
        NyxIdUserServiceRouteMutationAuthority mutationAuthority,
        string? exactUserServiceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutationAuthority);
        var before = await _converger.ReadAsync(mutationAuthority, cancellationToken)
            .ConfigureAwait(false);
        var resolution = NyxIdCodeExecutionRouteResolver.Resolve(before, exactUserServiceId);
        if (resolution.IsReady)
        {
            return new NyxIdCodeExecutionRouteReconciliation(
                resolution,
                Attempted: false,
                Verified: true);
        }

        var route = SelectRepairCandidate(before, exactUserServiceId);
        if (route is null)
        {
            return new NyxIdCodeExecutionRouteReconciliation(
                resolution,
                Attempted: false,
                Verified: resolution.IsReady);
        }

        var convergence = await _converger.ConvergeAsync(
                mutationAuthority,
                route.Id,
                RouteContract,
                before,
                cancellationToken)
            .ConfigureAwait(false);
        var verified = NyxIdCodeExecutionRouteResolver.Resolve(
            convergence.Snapshot,
            route.Id);
        var postconditionSatisfied = convergence.Verified && verified.IsReady;
        return new NyxIdCodeExecutionRouteReconciliation(
            verified,
            Attempted: convergence.Attempted,
            Verified: postconditionSatisfied,
            FailureKind: postconditionSatisfied
                ? NyxIdCodeExecutionRouteRepairFailureKind.None
                : convergence.FailureKind ==
                  NyxIdUserServiceRouteConvergenceFailureKind.UpdateException
                    ? NyxIdCodeExecutionRouteRepairFailureKind.UpdateException
                    : NyxIdCodeExecutionRouteRepairFailureKind.PostconditionMismatch);
    }

    private static NyxIdUserService? SelectRepairCandidate(
        NyxIdUserServiceAuthoritySnapshot snapshot,
        string? exactUserServiceId)
    {
        if (!snapshot.Succeeded)
            return null;

        var requestedId = string.IsNullOrWhiteSpace(exactUserServiceId)
            ? null
            : exactUserServiceId.Trim();
        var repairCandidates = snapshot.Routes.Value!.Services
            .Where(service =>
                string.Equals(
                    service.Slug,
                    Aevatar.AI.Abstractions.CodeExecution.CodeExecutionContract.ServiceSlug,
                    StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(service.CatalogServiceId) &&
                service.IsActive &&
                (requestedId is null ||
                 string.Equals(service.Id, requestedId, StringComparison.Ordinal)) &&
                snapshot.TryGetExact(service.Id, out var authority) &&
                authority is { CanManageRoute: true } &&
                string.Equals(
                    authority.Execution.CatalogServiceSlug,
                    Aevatar.AI.Abstractions.CodeExecution.CodeExecutionContract.ServiceSlug,
                    StringComparison.Ordinal))
            .ToArray();
        return repairCandidates.Length == 1 ? repairCandidates[0] : null;
    }
}
