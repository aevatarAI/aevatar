using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions.CodeExecution;

namespace Aevatar.AI.ToolProviders.NyxId;

public enum NyxIdCodeExecutionRouteRepairFailureKind
{
    None = 0,
    UpdateException = 1,
    PostconditionMismatch = 2,
    MutationRejected = 3,
}

public sealed record NyxIdCodeExecutionRouteReconciliation(
    NyxIdCodeExecutionRouteResolution Resolution,
    bool Attempted,
    bool Verified,
    NyxIdCodeExecutionRouteRepairFailureKind FailureKind =
        NyxIdCodeExecutionRouteRepairFailureKind.None,
    int HttpStatus = 0);

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
                    : convergence.FailureKind switch
                    {
                        NyxIdUserServiceRouteConvergenceFailureKind.UpdateException =>
                            NyxIdCodeExecutionRouteRepairFailureKind.UpdateException,
                        NyxIdUserServiceRouteConvergenceFailureKind.MutationRejected =>
                            NyxIdCodeExecutionRouteRepairFailureKind.MutationRejected,
                        _ => NyxIdCodeExecutionRouteRepairFailureKind.PostconditionMismatch,
                    },
                HttpStatus: postconditionSatisfied ? 0 : convergence.HttpStatus);
        }

        if (!TryGetPersonalRouteCreateTarget(
                before,
                resolution,
                exactUserServiceId,
                out var createNodeId))
        {
            return new NyxIdCodeExecutionRouteReconciliation(
                resolution,
                Attempted: false,
                Verified: false);
        }

        return await CreateAndVerifyPersonalRouteAsync(
                mutationAuthority,
                createNodeId,
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

    private static bool TryGetPersonalRouteCreateTarget(
        NyxIdUserServiceAuthoritySnapshot snapshot,
        NyxIdCodeExecutionRouteResolution resolution,
        string? exactUserServiceId,
        out string? nodeId)
    {
        nodeId = null;
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
                authority is { IsExecutionReady: true, Execution.AutoConnected: true } &&
                string.Equals(
                    authority.Execution.CatalogServiceSlug,
                    CodeExecutionContract.ServiceSlug,
                    StringComparison.Ordinal))
            .ToArray();
        if (canonical.Length != 1 ||
            !string.Equals(
                canonical[0].Slug,
                CodeExecutionContract.ServiceSlug,
                StringComparison.Ordinal) ||
            RouteContract.IsSatisfiedBy(canonical[0]) ||
            !snapshot.TryGetExact(canonical[0].Id, out var createAuthority) ||
            createAuthority is null)
        {
            return false;
        }

        nodeId = createAuthority.Execution.NodeId;
        return true;
    }

    private async Task<NyxIdCodeExecutionRouteReconciliation> CreateAndVerifyPersonalRouteAsync(
        NyxIdUserServiceRouteMutationAuthority mutationAuthority,
        string? nodeId,
        CancellationToken cancellationToken)
    {
        var createFailure = NyxIdCodeExecutionRouteRepairFailureKind.None;
        var createHttpStatus = 0;
        try
        {
            var body = JsonSerializer.Serialize(new CreatePersonalRouteRequest(
                CodeExecutionContract.ServiceSlug,
                CodeExecutionContract.PersonalServiceSlug,
                "Aevatar Code Execution",
                true,
                true,
                "proxy:* sandbox:execute",
                nodeId));
            var response = await _clientFactory.CreateClient()
                .CreateServiceResponseAsync(
                    mutationAuthority.BearerToken,
                    body,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!response.Succeeded && response.HttpStatus != (int)HttpStatusCode.Conflict)
            {
                createFailure = NyxIdCodeExecutionRouteRepairFailureKind.MutationRejected;
                createHttpStatus = response.HttpStatus;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            createFailure = NyxIdCodeExecutionRouteRepairFailureKind.UpdateException;
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
                : createFailure == NyxIdCodeExecutionRouteRepairFailureKind.None
                    ? NyxIdCodeExecutionRouteRepairFailureKind.PostconditionMismatch
                    : createFailure,
            HttpStatus: verified ? 0 : createHttpStatus);
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
                service.CredentialSource.Kind == NyxIdUserServiceCredentialSourceKind.Personal &&
                snapshot.TryGetExact(service.Id, out var authority) &&
                authority is { IsExecutionReady: true, Execution.AutoConnected: false } &&
                string.Equals(
                    authority.Execution.CatalogServiceSlug,
                    CodeExecutionContract.ServiceSlug,
                    StringComparison.Ordinal) &&
                RouteContract.IsSatisfiedBy(service))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private sealed record CreatePersonalRouteRequest(
        [property: JsonPropertyName("service_slug")] string ServiceSlug,
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("forward_access_token")] bool ForwardAccessToken,
        [property: JsonPropertyName("inject_delegation_token")] bool InjectDelegationToken,
        [property: JsonPropertyName("delegation_token_scope")] string DelegationTokenScope,
        [property: JsonPropertyName("node_id")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? NodeId);
}
