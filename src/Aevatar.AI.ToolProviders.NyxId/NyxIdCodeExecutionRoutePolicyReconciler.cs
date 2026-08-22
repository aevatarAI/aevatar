using System.Text.Json;
using Aevatar.AI.Abstractions.CodeExecution;

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
        NyxIdUserServiceBooleanRequirement.Enabled,
        NyxIdUserServiceBooleanRequirement.Enabled,
        ["proxy:*", "sandbox:execute"]);

    private readonly INyxIdApiClientFactory _clientFactory;
    private readonly NyxIdUserServiceRouteConverger _converger;

    public NyxIdCodeExecutionRoutePolicyReconciler(INyxIdApiClientFactory clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _converger = new NyxIdUserServiceRouteConverger(_clientFactory);
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
        if (route is not null)
        {
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

        if (!CanCreatePersonalRoute(before, resolution, exactUserServiceId))
        {
            return new NyxIdCodeExecutionRouteReconciliation(
                resolution,
                Attempted: false,
                Verified: false);
        }

        return await CreateAndVerifyPersonalRouteAsync(
                mutationAuthority,
                cancellationToken)
            .ConfigureAwait(false);
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
                CodeExecutionContract.IsSupportedServiceSlug(service.Slug) &&
                !string.IsNullOrWhiteSpace(service.CatalogServiceId) &&
                service.IsActive &&
                (requestedId is null ||
                 string.Equals(service.Id, requestedId, StringComparison.Ordinal)) &&
                snapshot.TryGetExact(service.Id, out var authority) &&
                authority is { CanManageRoute: true } &&
                string.Equals(
                    authority.Execution.CatalogServiceSlug,
                    CodeExecutionContract.ServiceSlug,
                    StringComparison.Ordinal))
            .ToArray();
        return repairCandidates.Length == 1 ? repairCandidates[0] : null;
    }

    private static bool CanCreatePersonalRoute(
        NyxIdUserServiceAuthoritySnapshot snapshot,
        NyxIdCodeExecutionRouteResolution resolution,
        string? exactUserServiceId)
    {
        if (!snapshot.Succeeded ||
            !string.IsNullOrWhiteSpace(exactUserServiceId) ||
            resolution.Kind != NyxIdCodeExecutionRouteResolutionKind.PolicyMismatch)
        {
            return false;
        }

        var canonical = snapshot.Routes.Value!.Services
            .Where(service =>
                CodeExecutionContract.IsSupportedServiceSlug(service.Slug) &&
                snapshot.TryGetExact(service.Id, out var authority) &&
                authority is { IsExecutionReady: true } &&
                string.Equals(
                    authority.Execution.CatalogServiceSlug,
                    CodeExecutionContract.ServiceSlug,
                    StringComparison.Ordinal))
            .ToArray();
        return canonical.Length == 1 &&
               canonical[0].AutoConnected &&
               string.Equals(
                   canonical[0].Slug,
                   CodeExecutionContract.ServiceSlug,
                   StringComparison.Ordinal) &&
               !RouteContract.IsSatisfiedBy(canonical[0]);
    }

    private async Task<NyxIdCodeExecutionRouteReconciliation> CreateAndVerifyPersonalRouteAsync(
        NyxIdUserServiceRouteMutationAuthority mutationAuthority,
        CancellationToken cancellationToken)
    {
        var createFailed = false;
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                service_slug = CodeExecutionContract.ServiceSlug,
                slug = CodeExecutionContract.PersonalServiceSlug,
                forward_access_token = true,
                inject_delegation_token = true,
                delegation_token_scope = "proxy:* sandbox:execute",
            });
            _ = await _clientFactory.CreateClient()
                .CreateServiceAsync(
                    mutationAuthority.BearerToken,
                    body,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            createFailed = true;
        }

        var after = await _converger.ReadAsync(mutationAuthority, cancellationToken)
            .ConfigureAwait(false);
        var personalRoute = SelectVerifiedPersonalRoute(after);
        var verifiedResolution = personalRoute is null
            ? NyxIdCodeExecutionRouteResolver.Resolve(after)
            : NyxIdCodeExecutionRouteResolver.Resolve(after, personalRoute.Id);
        var verified = personalRoute is not null && verifiedResolution.IsReady;
        return new NyxIdCodeExecutionRouteReconciliation(
            verifiedResolution,
            Attempted: true,
            Verified: verified,
            FailureKind: verified
                ? NyxIdCodeExecutionRouteRepairFailureKind.None
                : createFailed
                    ? NyxIdCodeExecutionRouteRepairFailureKind.UpdateException
                    : NyxIdCodeExecutionRouteRepairFailureKind.PostconditionMismatch);
    }

    private static NyxIdUserService? SelectVerifiedPersonalRoute(
        NyxIdUserServiceAuthoritySnapshot snapshot)
    {
        if (!snapshot.Succeeded)
            return null;

        var candidates = snapshot.Routes.Value!.Services
            .Where(service =>
                string.Equals(
                    service.Slug,
                    CodeExecutionContract.PersonalServiceSlug,
                    StringComparison.Ordinal) &&
                !service.AutoConnected &&
                service.CredentialSource.Kind == NyxIdUserServiceCredentialSourceKind.Personal &&
                snapshot.TryGetExact(service.Id, out var authority) &&
                authority is { IsExecutionReady: true } &&
                string.Equals(
                    authority.Execution.CatalogServiceSlug,
                    CodeExecutionContract.ServiceSlug,
                    StringComparison.Ordinal) &&
                RouteContract.IsSatisfiedBy(service))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
}
