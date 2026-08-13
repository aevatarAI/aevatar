using System.Text.Json;

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
/// Reconciles a caller-owned personal code route to the platform's delegation-only policy. The
/// result is based on a fresh authoritative inventory read, never on the management response.
/// </summary>
public sealed class NyxIdCodeExecutionRoutePolicyReconciler(
    INyxIdApiClientFactory clientFactory)
{
    public async Task<NyxIdCodeExecutionRouteReconciliation> ReconcileAsync(
        string bearerToken,
        string? exactUserServiceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        var client = clientFactory.CreateClient();
        var before = await ReadAsync(client, bearerToken, cancellationToken).ConfigureAwait(false);
        var resolution = NyxIdCodeExecutionRouteResolver.Resolve(before, exactUserServiceId);
        if (resolution.IsReady)
        {
            return new NyxIdCodeExecutionRouteReconciliation(
                resolution,
                Attempted: false,
                Verified: true);
        }

        var route = SelectPersonalRepairCandidate(before, exactUserServiceId);
        if (route is null ||
            route.CredentialSource.Kind != NyxIdUserServiceCredentialSourceKind.Personal)
        {
            return new NyxIdCodeExecutionRouteReconciliation(
                resolution,
                Attempted: false,
                Verified: resolution.IsReady);
        }

        var desiredScope = NyxIdCodeExecutionRouteResolver.AddCodeExecutionDelegationScope(
            route.DelegationTokenScope);
        if (IsCanonicalPolicy(route, desiredScope))
        {
            return new NyxIdCodeExecutionRouteReconciliation(
                resolution,
                Attempted: false,
                Verified: true);
        }

        var updateFailureKind = NyxIdCodeExecutionRouteRepairFailureKind.None;
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                forward_access_token = false,
                inject_delegation_token = true,
                delegation_token_scope = desiredScope,
            });
            _ = await client.UpdateServiceRouteAsync(
                    bearerToken,
                    route.Id,
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
            updateFailureKind = NyxIdCodeExecutionRouteRepairFailureKind.UpdateException;
        }

        var after = await ReadAsync(client, bearerToken, cancellationToken).ConfigureAwait(false);
        var verified = NyxIdCodeExecutionRouteResolver.Resolve(after, route.Id);
        var postconditionSatisfied = verified.IsReady &&
                                     string.Equals(
                                         verified.Service!.CatalogServiceId,
                                         route.CatalogServiceId,
                                         StringComparison.Ordinal) &&
                                     verified.Service.ForwardAccessToken == false &&
                                     verified.Service.InjectDelegationToken == true &&
                                     GrantsEveryScope(
                                         verified.Service.DelegationTokenScope,
                                         desiredScope);
        return new NyxIdCodeExecutionRouteReconciliation(
            verified,
            Attempted: true,
            Verified: postconditionSatisfied,
            FailureKind: postconditionSatisfied
                ? NyxIdCodeExecutionRouteRepairFailureKind.None
                : updateFailureKind == NyxIdCodeExecutionRouteRepairFailureKind.None
                    ? NyxIdCodeExecutionRouteRepairFailureKind.PostconditionMismatch
                    : updateFailureKind);
    }

    private static async Task<NyxIdApiAccessResult<NyxIdUserServices>> ReadAsync(
        NyxIdApiClient client,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        var response = await client.ListUserServicesAsync(bearerToken, cancellationToken)
            .ConfigureAwait(false);
        return NyxIdApiAccessResponseParser.ParseCodeExecutionUserServices(response);
    }

    private static bool IsCanonicalPolicy(NyxIdUserService route, string desiredScope) =>
        route.ForwardAccessToken == false &&
        route.InjectDelegationToken == true &&
        GrantsEveryScope(route.DelegationTokenScope, desiredScope);

    private static NyxIdUserService? SelectPersonalRepairCandidate(
        NyxIdApiAccessResult<NyxIdUserServices> inventory,
        string? exactUserServiceId)
    {
        if (!inventory.Succeeded)
            return null;

        var requestedId = string.IsNullOrWhiteSpace(exactUserServiceId)
            ? null
            : exactUserServiceId.Trim();
        var personalCandidates = inventory.Value!.Services
            .Where(service =>
                string.Equals(
                    service.Slug,
                    Aevatar.AI.Abstractions.CodeExecution.CodeExecutionContract.ServiceSlug,
                    StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(service.CatalogServiceId) &&
                service.IsActive &&
                service.CredentialSource.Kind == NyxIdUserServiceCredentialSourceKind.Personal &&
                (requestedId is null || string.Equals(service.Id, requestedId, StringComparison.Ordinal)))
            .ToArray();
        return personalCandidates.Length == 1 ? personalCandidates[0] : null;
    }

    private static bool GrantsEveryScope(string? actual, string required)
    {
        var actualScopes = SplitScopes(actual).ToHashSet(StringComparer.Ordinal);
        return SplitScopes(required).All(actualScopes.Contains);
    }

    private static IEnumerable<string> SplitScopes(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
